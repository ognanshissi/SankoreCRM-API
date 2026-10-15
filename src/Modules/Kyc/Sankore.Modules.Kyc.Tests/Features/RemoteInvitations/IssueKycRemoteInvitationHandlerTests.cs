namespace Sankore.Modules.Kyc.Tests.Features.RemoteInvitations;

using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.RemoteInvitations;
using Sankore.Modules.Kyc.Features.RemoteInvitations.IssueInvitation;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Xunit;

/// <summary>
/// KRM-B-03: who gets a collection link at a conversion, who does not, and why (KRM-B-04 for the
/// replacement of one link by another).
///
/// <para>
/// The three refusals are the substance of this story rather than its edge cases. A product that
/// has not enabled remote KYC is the DEFAULT state of every catalogue line, and a lead captured in
/// the field with a phone number and no email is the ordinary case in this market — so the feature
/// is mostly about not sending a link, and about saying which of the two it was.
/// </para>
/// </summary>
public sealed class IssueKycRemoteInvitationHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly Guid _productLineId = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();

    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;

    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IKycSettings _settings = Substitute.For<IKycSettings>();
    private readonly IBackgroundJobClient _hangfire = Substitute.For<IBackgroundJobClient>();

    public IssueKycRemoteInvitationHandlerTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();

        _customers.HasEmailAsync(_tenantId, _customerId, Arg.Any<CancellationToken>())
            .Returns(true);

        _settings.GetIntAsync(_tenantId, KycSettingKeys.RemoteEmailDelayMinutes,
                Arg.Any<CancellationToken>())
            .Returns(5);

        _administration.GetKycRemotePolicyAsync(_tenantId, _productLineId, Arg.Any<CancellationToken>())
            .Returns(Policy());
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    private KycRemotePolicyDto Policy(bool enabled = true, int validityHours = 72) => new(
        ProductLineId: _productLineId,
        ProductCode: "SANTE-01",
        ProductName: "Assurance santé",
        Category: "Insurance",
        IsActive: true,
        Enabled: enabled,
        ValidationMode: "Manual",
        RequiredTier: "Simplified",
        MinAutoScore: 85,
        RequiredDocuments: ["ProofOfAddress"],
        AllowedDocumentTypes: ["Cni"],
        LinkValidityHours: validityHours);

    private IssueKycRemoteInvitationHandler Handler() => new(
        _db,
        new KycRemoteEligibility(_db, _administration, _customers),
        _settings,
        _hangfire,
        TimeProvider.System,
        NullLogger<IssueKycRemoteInvitationHandler>.Instance);

    private async Task<KycFile> SeedFileAsync(
        KycFileStatus status = KycFileStatus.Collecting,
        Guid? productLineId = null)
    {
        var file = KycFile.Open(
            _tenantId, _customerId, KycChannel.LeadConversion, _agentId, TimeProvider.System,
            productLineId: productLineId ?? _productLineId,
            responsibleAgentId: _agentId);

        // The statuses this story cares about are reached through the transition table, not by
        // assignment: a test that forced the column would also be testing a state the aggregate
        // cannot produce.
        if (status == KycFileStatus.ComplementRequired)
        {
            file.SubmitForVerification(_agentId, TimeProvider.System);
            file.RequestComplement(TimeProvider.System);
        }
        else if (status == KycFileStatus.Verifying)
        {
            file.SubmitForVerification(_agentId, TimeProvider.System);
        }

        _db.KycFiles.Add(file);
        await _db.SaveChangesAsync(CancellationToken.None);
        return file;
    }

    private IssueKycRemoteInvitationCommand Command(
        Guid kycFileId, bool replaceActive = false, Guid? issuedBy = null, int? delayMinutes = null)
        => new(
            TenantId: _tenantId,
            KycFileId: kycFileId,
            IssuedBy: issuedBy ?? Guid.Empty,
            ReplaceActive: replaceActive,
            DelayMinutes: delayMinutes);

    // ── The happy path ────────────────────────────────────────────────────

    [Fact]
    public async Task A_link_is_issued_Scheduled_with_no_token_yet()
    {
        var file = await SeedFileAsync();

        var result = await Handler().Handle(Command(file.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Reused.Should().BeFalse();

        var invitation = await _db.KycRemoteInvitations.SingleAsync(CancellationToken.None);
        invitation.Status.Should().Be(KycRemoteInvitationStatus.Scheduled);
        invitation.Purpose.Should().Be(KycRemoteInvitationPurpose.Initial);

        // The whole point of minting at send time: there is nothing walkable in the database
        // between the conversion and the email, so the token never travels through a Hangfire
        // payload.
        invitation.TokenHash.Should().BeNull();
    }

    [Fact]
    public async Task The_link_lifetime_comes_from_the_product_and_not_from_a_constant()
    {
        _administration.GetKycRemotePolicyAsync(_tenantId, _productLineId, Arg.Any<CancellationToken>())
            .Returns(Policy(validityHours: 24));

        var file = await SeedFileAsync();
        var before = DateTimeOffset.UtcNow;

        var result = await Handler().Handle(Command(file.Id), CancellationToken.None);

        result.Value.ExpiresAt.Should().BeCloseTo(before.AddHours(24), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task The_send_is_delayed_by_the_tenant_setting_so_the_welcome_email_lands_first()
    {
        var file = await SeedFileAsync();

        await Handler().Handle(Command(file.Id), CancellationToken.None);

        // Scheduled, not enqueued: the customer is asked for a client number that arrives in the
        // welcome message, and the two are queued within the same second by two modules.
        _hangfire.Received().Create(
            Arg.Any<Job>(),
            Arg.Is<IState>(state => state is ScheduledState));
    }

    [Fact]
    public async Task An_agent_re_issuing_gets_the_email_now()
    {
        var file = await SeedFileAsync();

        await Handler().Handle(
            Command(file.Id, replaceActive: true, issuedBy: _agentId, delayMinutes: 0),
            CancellationToken.None);

        _hangfire.Received().Create(
            Arg.Any<Job>(),
            Arg.Is<IState>(state => state is EnqueuedState));
    }

    [Fact]
    public async Task A_complement_file_may_still_be_sent_a_link()
    {
        // A file sent back for a better photo is exactly where a second link saves a branch visit.
        var file = await SeedFileAsync(KycFileStatus.ComplementRequired);

        var result = await Handler().Handle(Command(file.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    // ── The three refusals ────────────────────────────────────────────────

    [Fact]
    public async Task A_file_with_no_product_falls_back_to_the_default_policy()
    {
        // A counter enrolment, or a lead whose free-text product label matched no catalogue entry.
        //
        // This REPLACES a case that pinned the opposite — a refusal — and the reversal is the
        // point: two of the three ways a file is opened carry no product line, including the
        // back-office endpoint, so refusing here told an agent that "the product does not allow
        // it" about a product nobody had chosen, and closed remote collection to every file
        // opened from the KYC screen. See KycRemoteDefaultPolicy for what the fallback concedes.
        var file = await SeedFileAsync(productLineId: Guid.Empty);

        var result = await Handler().Handle(Command(file.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var issued = await _db.KycRemoteInvitations.SingleAsync(CancellationToken.None);
        issued.KycFileId.Should().Be(file.Id);

        // La durée vient de la politique par défaut, pas d'un produit : 72 heures.
        (issued.ExpiresAt - issued.CreatedAt).TotalHours.Should().BeApproximately(72, 0.01);
    }

    [Fact]
    public async Task A_product_that_has_not_enabled_remote_kyc_is_refused()
    {
        // The DEFAULT state of every catalogue line, which is why this is the first refusal to
        // read correctly on the agent's screen.
        _administration.GetKycRemotePolicyAsync(_tenantId, _productLineId, Arg.Any<CancellationToken>())
            .Returns(Policy(enabled: false));

        var file = await SeedFileAsync();

        var result = await Handler().Handle(Command(file.Id), CancellationToken.None);

        result.Error.Should().Be(KycErrors.RemoteInvitationProductNotEligible);
    }

    [Fact]
    public async Task A_dangling_product_reference_is_refused_the_same_way()
    {
        _administration.GetKycRemotePolicyAsync(_tenantId, _productLineId, Arg.Any<CancellationToken>())
            .Returns((KycRemotePolicyDto?)null);

        var file = await SeedFileAsync();

        var result = await Handler().Handle(Command(file.Id), CancellationToken.None);

        result.Error.Should().Be(KycErrors.RemoteInvitationProductNotEligible);
    }

    [Fact]
    public async Task A_customer_with_no_email_is_refused_and_named_as_such()
    {
        // The one refusal the agent can fix themselves, which is why it has its own code.
        _customers.HasEmailAsync(_tenantId, _customerId, Arg.Any<CancellationToken>())
            .Returns(false);

        var file = await SeedFileAsync();

        var result = await Handler().Handle(Command(file.Id), CancellationToken.None);

        result.Error.Should().Be(KycErrors.RemoteInvitationNoEmail);
    }

    [Fact]
    public async Task A_file_that_is_no_longer_collecting_is_refused()
    {
        var file = await SeedFileAsync(KycFileStatus.Verifying);

        var result = await Handler().Handle(Command(file.Id), CancellationToken.None);

        result.Error.Should().Be(KycErrors.RemoteInvitationFileNotCollecting);
    }

    [Fact]
    public async Task An_unknown_file_is_refused()
    {
        var result = await Handler().Handle(Command(Guid.NewGuid()), CancellationToken.None);

        result.Error.Should().Be(KycErrors.RemoteInvitationNotFound);
    }

    [Fact]
    public async Task A_refusal_queues_no_email()
    {
        _customers.HasEmailAsync(_tenantId, _customerId, Arg.Any<CancellationToken>())
            .Returns(false);

        var file = await SeedFileAsync();

        await Handler().Handle(Command(file.Id), CancellationToken.None);

        _hangfire.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
    }

    // ── One active link per file (KRM-B-04) ───────────────────────────────

    [Fact]
    public async Task A_redelivered_conversion_event_reuses_the_link_and_mails_nothing()
    {
        var file = await SeedFileAsync();

        var first = await Handler().Handle(Command(file.Id), CancellationToken.None);
        _hangfire.ClearReceivedCalls();

        var second = await Handler().Handle(Command(file.Id), CancellationToken.None);

        second.IsSuccess.Should().BeTrue();
        second.Value.Reused.Should().BeTrue();
        second.Value.InvitationId.Should().Be(first.Value.InvitationId,
            "a second link would both spam the customer and invalidate the one they are reading");

        _hangfire.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
        (await _db.KycRemoteInvitations.CountAsync(CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task An_agent_re_issuing_revokes_the_previous_link()
    {
        var file = await SeedFileAsync();
        var first = await Handler().Handle(Command(file.Id), CancellationToken.None);

        var second = await Handler().Handle(
            Command(file.Id, replaceActive: true, issuedBy: _agentId, delayMinutes: 0),
            CancellationToken.None);

        second.Value.InvitationId.Should().NotBe(first.Value.InvitationId);

        var previous = await _db.KycRemoteInvitations
            .FirstAsync(i => i.Id == first.Value.InvitationId, CancellationToken.None);

        previous.Status.Should().Be(KycRemoteInvitationStatus.Revoked);
        previous.RevokeReason.Should().NotBeNullOrWhiteSpace(
            "a link disappearing from a customer's reach with no motive is a support call nobody can close");
        previous.RevokedBy.Should().Be(_agentId);

        // Exactly one live link at any moment: an intercepted older one must hold nothing.
        var active = await _db.KycRemoteInvitations
            .CountAsync(i => i.Status == KycRemoteInvitationStatus.Scheduled, CancellationToken.None);
        active.Should().Be(1);
    }

    [Fact]
    public async Task An_expired_link_is_replaced_even_without_asking()
    {
        // The sweep has not run yet, or the customer came back a week later. The caller asked for
        // the customer to hold a usable link and the lapsed one is not — so it is replaced rather
        // than reported as reusable.
        var file = await SeedFileAsync();

        var stale = KycRemoteInvitation.Issue(
            _tenantId, file.Id, _customerId, validityHours: 1, createdBy: Guid.Empty,
            clock: new FrozenClock(DateTimeOffset.UtcNow.AddDays(-3)));

        _db.KycRemoteInvitations.Add(stale);
        await _db.SaveChangesAsync(CancellationToken.None);

        var result = await Handler().Handle(Command(file.Id), CancellationToken.None);

        result.Value.Reused.Should().BeFalse();
        result.Value.InvitationId.Should().NotBe(stale.Id);

        var reloaded = await _db.KycRemoteInvitations
            .FirstAsync(i => i.Id == stale.Id, CancellationToken.None);
        reloaded.Status.Should().Be(KycRemoteInvitationStatus.Revoked);
    }

    [Fact]
    public async Task The_automatic_issue_is_recorded_as_the_systems_and_not_the_agents()
    {
        // The row has to answer "did a human ask for this credential to be posted?".
        var file = await SeedFileAsync();

        await Handler().Handle(Command(file.Id), CancellationToken.None);

        var invitation = await _db.KycRemoteInvitations.SingleAsync(CancellationToken.None);
        invitation.CreatedBy.Should().Be(Guid.Empty);
    }

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

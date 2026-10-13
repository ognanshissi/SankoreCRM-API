namespace Sankore.Modules.Kyc.Tests.Features.RemoteInvitations;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Files;
using Sankore.Modules.Kyc.Features.RemoteInvitations;
using Sankore.Modules.Kyc.Features.RemoteInvitations.ListInvitations;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

/// <summary>
/// The agent's "KYC à distance" panel (KRM-F-07, over KRM-B-04).
///
/// <para>
/// Two things make this read worth its own cases. The REASON no link can be issued is computed on
/// every call rather than stored, so it disappears the moment an agent types the missing email in —
/// a stored reason would keep telling them "pas d'email" minutes afterwards. And the panel offers a
/// button that MAILS a bearer credential, so the agency perimeter has to be checked here and
/// reported as not-found, exactly as on every other read of a file.
/// </para>
/// </summary>
public sealed class ListKycRemoteInvitationsHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly Guid _productLineId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();

    public ListKycRemoteInvitationsHandlerTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();

        _administration.GetKycRemotePolicyAsync(_tenantId, _productLineId, Arg.Any<CancellationToken>())
            .Returns(Policy());

        _customers.HasEmailAsync(_tenantId, _customerId, Arg.Any<CancellationToken>()).Returns(true);
        _customers.GetMaskedEmailAsync(_tenantId, _customerId, Arg.Any<CancellationToken>())
            .Returns("a***@e***.com");
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    private KycRemotePolicyDto Policy(bool enabled = true) => new(
        _productLineId, "SANTE-01", "Assurance santé", "Insurance", true,
        enabled, "Manual", "Simplified", 85, [], ["Cni"], LinkValidityHours: 48);

    private ListKycRemoteInvitationsHandler Handler(params Guid[] accessibleAgencies)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Id.Returns(_userId);
        currentUser.TenantId.Returns(_tenantId);

        return new ListKycRemoteInvitationsHandler(
            _db,
            new KycRemoteEligibility(_db, _administration, _customers),
            _customers,
            currentUser,
            new KycFileAccess(_db, currentUser, new FakeAgencyScope(accessibleAgencies)));
    }

    private async Task<KycFile> SeedFileAsync(Guid? agencyId = null)
    {
        var file = KycFile.Open(
            _tenantId, _customerId, KycChannel.LeadConversion, Guid.NewGuid(), TimeProvider.System,
            agencyId: agencyId ?? _agencyId,
            productLineId: _productLineId,
            responsibleAgentId: Guid.NewGuid());

        _db.KycFiles.Add(file);
        await _db.SaveChangesAsync(CancellationToken.None);
        return file;
    }

    [Fact]
    public async Task An_eligible_file_with_no_link_yet_says_a_link_can_be_issued()
    {
        var file = await SeedFileAsync();

        var result = await Handler().Handle(
            new ListKycRemoteInvitationsQuery(file.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Invitations.Should().BeEmpty();
        result.Value.CanIssue.Should().BeTrue();
        result.Value.UnavailableReason.Should().BeNull();
        result.Value.RecipientEmailMasked.Should().Be("a***@e***.com");
        result.Value.LinkValidityHours.Should().Be(48,
            "the screen must show the lifetime the server will actually use, not a hard-coded 72");
    }

    [Fact]
    public async Task A_customer_with_no_email_is_told_so_and_shown_no_recipient()
    {
        _customers.HasEmailAsync(_tenantId, _customerId, Arg.Any<CancellationToken>()).Returns(false);

        var file = await SeedFileAsync();

        var result = await Handler().Handle(
            new ListKycRemoteInvitationsQuery(file.Id), CancellationToken.None);

        result.Value.CanIssue.Should().BeFalse();
        result.Value.UnavailableReason.Should().Be(KycErrors.RemoteInvitationNoEmail);
        result.Value.RecipientEmailMasked.Should().BeNull(
            "there is no address to confirm, and this is the one call on the panel that decrypts");
    }

    [Fact]
    public async Task An_ineligible_product_is_named_as_the_cause()
    {
        _administration.GetKycRemotePolicyAsync(_tenantId, _productLineId, Arg.Any<CancellationToken>())
            .Returns(Policy(enabled: false));

        var file = await SeedFileAsync();

        var result = await Handler().Handle(
            new ListKycRemoteInvitationsQuery(file.Id), CancellationToken.None);

        result.Value.UnavailableReason
            .Should().Be(KycErrors.RemoteInvitationProductNotEligible);
    }

    [Fact]
    public async Task The_reason_disappears_as_soon_as_the_cause_is_fixed()
    {
        // The whole argument for computing it on every read: an agent who adds the missing address
        // must not be told "pas d'email" on their next refresh.
        var file = await SeedFileAsync();

        _customers.HasEmailAsync(_tenantId, _customerId, Arg.Any<CancellationToken>()).Returns(false);

        var before = await Handler().Handle(
            new ListKycRemoteInvitationsQuery(file.Id), CancellationToken.None);
        before.Value.UnavailableReason.Should().Be(KycErrors.RemoteInvitationNoEmail);

        _customers.HasEmailAsync(_tenantId, _customerId, Arg.Any<CancellationToken>()).Returns(true);

        var after = await Handler().Handle(
            new ListKycRemoteInvitationsQuery(file.Id), CancellationToken.None);
        after.Value.UnavailableReason.Should().BeNull();
        after.Value.CanIssue.Should().BeTrue();
    }

    [Fact]
    public async Task Links_are_listed_newest_first_and_carry_no_token()
    {
        var file = await SeedFileAsync();

        var older = KycRemoteInvitation.Issue(
            _tenantId, file.Id, _customerId, 72, Guid.Empty,
            new FrozenClock(DateTimeOffset.UtcNow.AddDays(-2)));
        older.Revoke("Remplacé", null, TimeProvider.System);

        var newer = KycRemoteInvitation.Issue(
            _tenantId, file.Id, _customerId, 72, _userId, TimeProvider.System);

        _db.KycRemoteInvitations.AddRange(older, newer);
        await _db.SaveChangesAsync(CancellationToken.None);

        var result = await Handler().Handle(
            new ListKycRemoteInvitationsQuery(file.Id), CancellationToken.None);

        result.Value.Invitations.Select(i => i.Id).Should().Equal(newer.Id, older.Id);

        // There is deliberately no way to learn what a link WAS. The screen says how far the
        // customer got; an agent who needs them to start again issues a new one.
        var dto = typeof(KycRemoteInvitationDto);
        dto.GetProperty("TokenHash").Should().BeNull();
        dto.GetProperty("Token").Should().BeNull();
    }

    [Fact]
    public async Task A_system_issued_link_is_distinguished_from_an_agents()
    {
        var file = await SeedFileAsync();

        var automatic = KycRemoteInvitation.Issue(
            _tenantId, file.Id, _customerId, 72, Guid.Empty, TimeProvider.System);

        _db.KycRemoteInvitations.Add(automatic);
        await _db.SaveChangesAsync(CancellationToken.None);

        var result = await Handler().Handle(
            new ListKycRemoteInvitationsQuery(file.Id), CancellationToken.None);

        result.Value.Invitations.Single().IsSystemIssued.Should().BeTrue();
    }

    [Fact]
    public async Task A_file_outside_the_perimeter_is_reported_as_not_found()
    {
        // NOT forbidden: a 403 would confirm another branch's file exists, and this panel offers a
        // button that mails a credential into it.
        var file = await SeedFileAsync();

        var result = await Handler(Guid.NewGuid()).Handle(
            new ListKycRemoteInvitationsQuery(file.Id), CancellationToken.None);

        result.Error.Should().Be(KycErrors.FileNotFound);
    }

    [Fact]
    public async Task A_file_with_no_agency_is_invisible_to_a_restricted_caller()
    {
        // Failing closed: an unresolved agency must not become everyone's — see KycFile.AgencyId.
        var file = await SeedFileAsync(agencyId: Guid.Empty);

        var restricted = await Handler(_agencyId).Handle(
            new ListKycRemoteInvitationsQuery(file.Id), CancellationToken.None);
        restricted.Error.Should().Be(KycErrors.FileNotFound);

        var unrestricted = await Handler().Handle(
            new ListKycRemoteInvitationsQuery(file.Id), CancellationToken.None);
        unrestricted.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_file_is_reported_as_not_found()
    {
        var result = await Handler().Handle(
            new ListKycRemoteInvitationsQuery(Guid.NewGuid()), CancellationToken.None);

        result.Error.Should().Be(KycErrors.FileNotFound);
    }

    private sealed class FakeAgencyScope(Guid[] accessible) : IAgencyScopeProvider
    {
        private readonly HashSet<Guid>? _accessible =
            accessible.Length == 0 ? null : [.. accessible];

        public Task<IReadOnlySet<Guid>?> GetAccessibleAgencyIdsAsync(
            Guid tenantId, Guid userId, CancellationToken ct)
            => Task.FromResult<IReadOnlySet<Guid>?>(_accessible);

        public Task<bool> CanAccessAgencyAsync(
            Guid tenantId, Guid userId, Guid agencyId, CancellationToken ct)
            => Task.FromResult(_accessible is null || _accessible.Contains(agencyId));
    }

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

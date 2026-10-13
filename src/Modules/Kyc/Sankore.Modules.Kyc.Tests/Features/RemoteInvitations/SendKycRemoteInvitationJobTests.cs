namespace Sankore.Modules.Kyc.Tests.Features.RemoteInvitations;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.RemoteInvitations;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.RemoteLinks;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// KRM-B-03, the send: the token is minted here, the hash is stored here, and the clear value
/// never leaves this method.
///
/// <para>
/// The ordering assertions are the ones worth having. The row is committed BEFORE the mail is
/// queued because the two cannot be atomic, and the choice of which failure to survive is the
/// design: a lost mail leaves a working link the agent can re-send, while a mail sent for a hash
/// that was never stored gives the customer a link guaranteed to fail.
/// </para>
/// </summary>
public sealed class SendKycRemoteInvitationJobTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();

    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly ITenantStore _tenants = Substitute.For<ITenantStore>();
    private readonly List<NotifyClientRequest> _sent = [];

    public SendKycRemoteInvitationJobTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();

        _tenants.GetAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantInfo(
                _tenantId, "Sankore MFI", "imf.sankore.app", true, false, null, null, "fr"));

        _customers.NotifyClientAsync(
                Arg.Do<NotifyClientRequest>(_sent.Add), Arg.Any<CancellationToken>())
            .Returns(true);

        _administration.GetAgencyAsync(_tenantId, _agencyId, Arg.Any<CancellationToken>())
            .Returns(new AgencySummary(_agencyId, "ABJ-PLT", "Abidjan Plateau", null, true));
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    private SendKycRemoteInvitationJob Job()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton<IKycRemoteLinkTokens, KycRemoteLinkTokens>();
        services.AddSingleton(_customers);
        services.AddSingleton(_administration);
        services.AddSingleton(_tenants);
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();

        return new SendKycRemoteInvitationJob(new FixedScopeFactory(services.BuildServiceProvider()));
    }

    private async Task<(KycFile File, KycRemoteInvitation Invitation)> SeedAsync()
    {
        var file = KycFile.Open(
            _tenantId, _customerId, KycChannel.LeadConversion, Guid.NewGuid(), TimeProvider.System,
            agencyId: _agencyId, productLineId: Guid.NewGuid(), responsibleAgentId: Guid.NewGuid());

        var invitation = KycRemoteInvitation.Issue(
            _tenantId, file.Id, _customerId, validityHours: 72, createdBy: Guid.Empty,
            clock: TimeProvider.System);

        _db.KycFiles.Add(file);
        _db.KycRemoteInvitations.Add(invitation);
        await _db.SaveChangesAsync(CancellationToken.None);

        return (file, invitation);
    }

    [Fact]
    public async Task The_token_is_minted_stored_as_a_hash_and_mailed_as_a_link()
    {
        var (_, invitation) = await SeedAsync();

        await Job().ExecuteAsync(invitation.Id, _tenantId);

        var reloaded = await _db.KycRemoteInvitations
            .FirstAsync(i => i.Id == invitation.Id, CancellationToken.None);

        reloaded.Status.Should().Be(KycRemoteInvitationStatus.Sent);
        reloaded.SentAt.Should().NotBeNull();
        reloaded.TokenHash.Should().NotBeNull().And.HaveCount(32);

        var mail = _sent.Should().ContainSingle().Subject;
        mail.TemplateKey.Should().Be(SendKycRemoteInvitationJob.TemplateKey);
        mail.ClientId.Should().Be(_customerId);

        var link = mail.TemplateData!["link"].ToString()!;
        link.Should().StartWith("https://imf.sankore.app/kyc/verification#t=");

        // The hash in the row is the hash of the token that was mailed. If these ever disagree the
        // customer gets a link that cannot be found, and nothing else in the system notices.
        var token = link[(link.IndexOf("#t=", StringComparison.Ordinal) + 3)..];
        new KycRemoteLinkTokens().HashOrNull(token).Should().Equal(reloaded.TokenHash);
    }

    [Fact]
    public async Task The_email_carries_the_link_and_never_the_client_number()
    {
        // Two factors, two channels. The client number arrives in the welcome message and is the
        // secret the link is checked against: one intercepted email must not be enough to walk the
        // whole flow.
        var (_, invitation) = await SeedAsync();

        await Job().ExecuteAsync(invitation.Id, _tenantId);

        var data = _sent.Should().ContainSingle().Subject.TemplateData!;
        data.Keys.Should().BeEquivalentTo(["link", "expires_at", "agency_name"]);
        data["agency_name"].Should().Be("Abidjan Plateau");
    }

    [Fact]
    public async Task A_revoked_link_is_not_sent_and_gets_no_token()
    {
        // An agent who re-issued immediately produces exactly this: the scheduled job fires minutes
        // later against a link that has been superseded.
        var (_, invitation) = await SeedAsync();

        // AsTracking is not optional: KycDbContext is NoTracking by default, so a read without it
        // hands back a detached instance and the revocation below would write nothing.
        var tracked = await _db.KycRemoteInvitations
            .AsTracking()
            .FirstAsync(i => i.Id == invitation.Id, CancellationToken.None);
        tracked.Revoke("Remplacé par un nouveau lien", null, TimeProvider.System);
        await _db.SaveChangesAsync(CancellationToken.None);

        await Job().ExecuteAsync(invitation.Id, _tenantId);

        _sent.Should().BeEmpty();

        var reloaded = await _db.KycRemoteInvitations
            .FirstAsync(i => i.Id == invitation.Id, CancellationToken.None);
        reloaded.TokenHash.Should().BeNull();
    }

    [Fact]
    public async Task A_second_run_mints_no_second_token()
    {
        // Hangfire retries on its own schedule. A second mint would mail the customer twice AND
        // invalidate the link they already hold, which is the worse half.
        var (_, invitation) = await SeedAsync();

        await Job().ExecuteAsync(invitation.Id, _tenantId);

        var afterFirst = await _db.KycRemoteInvitations
            .AsNoTracking()
            .FirstAsync(i => i.Id == invitation.Id, CancellationToken.None);

        _sent.Clear();

        await Job().ExecuteAsync(invitation.Id, _tenantId);

        var afterSecond = await _db.KycRemoteInvitations
            .AsNoTracking()
            .FirstAsync(i => i.Id == invitation.Id, CancellationToken.None);

        afterSecond.TokenHash.Should().Equal(afterFirst.TokenHash);
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tenant_with_no_fqdn_sends_nothing_and_leaves_the_link_scheduled()
    {
        // A configuration fault: there is no host to build a link on. Left Scheduled so the
        // nightly sweep turns it into a visible Expired row rather than a silent nothing.
        _tenants.GetAsync(_tenantId, Arg.Any<CancellationToken>()).Returns((TenantInfo?)null);

        var (_, invitation) = await SeedAsync();

        await Job().ExecuteAsync(invitation.Id, _tenantId);

        _sent.Should().BeEmpty();

        var reloaded = await _db.KycRemoteInvitations
            .FirstAsync(i => i.Id == invitation.Id, CancellationToken.None);
        reloaded.Status.Should().Be(KycRemoteInvitationStatus.Scheduled);
        reloaded.TokenHash.Should().BeNull();
    }

    [Fact]
    public async Task A_mail_that_cannot_be_queued_still_leaves_a_working_link()
    {
        // The link is already minted and stored, so it WORKS — it may just not have reached
        // anybody. Unwinding the status would let a retry mint a second token and invalidate a link
        // that may well have been delivered after all.
        _customers.NotifyClientAsync(Arg.Any<NotifyClientRequest>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var (_, invitation) = await SeedAsync();

        await Job().ExecuteAsync(invitation.Id, _tenantId);

        var reloaded = await _db.KycRemoteInvitations
            .FirstAsync(i => i.Id == invitation.Id, CancellationToken.None);

        reloaded.Status.Should().Be(KycRemoteInvitationStatus.Sent);
        reloaded.TokenHash.Should().NotBeNull();
    }

    [Fact]
    public async Task An_invitation_that_no_longer_exists_is_not_an_incident()
    {
        await Job().ExecuteAsync(Guid.NewGuid(), _tenantId);

        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_with_no_agency_still_gets_its_link()
    {
        // KycFile.AgencyId is null for a file opened while M01 could not answer. A missing branch
        // name is a slightly poorer message; a missing link is a customer who cannot enrol.
        var file = KycFile.Open(
            _tenantId, _customerId, KycChannel.LeadConversion, Guid.NewGuid(), TimeProvider.System,
            agencyId: null, productLineId: Guid.NewGuid());

        var invitation = KycRemoteInvitation.Issue(
            _tenantId, file.Id, _customerId, 72, Guid.Empty, TimeProvider.System);

        _db.KycFiles.Add(file);
        _db.KycRemoteInvitations.Add(invitation);
        await _db.SaveChangesAsync(CancellationToken.None);

        await Job().ExecuteAsync(invitation.Id, _tenantId);

        var mail = _sent.Should().ContainSingle().Subject;
        mail.TemplateData!["agency_name"].Should().Be(string.Empty);
    }
}

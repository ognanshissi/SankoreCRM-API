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
    private readonly List<NotifyClientBySmsRequest> _texted = [];

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

        _customers.NotifyClientBySmsAsync(
                Arg.Do<NotifyClientBySmsRequest>(_texted.Add), Arg.Any<CancellationToken>())
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

    private async Task<(KycFile File, KycRemoteInvitation Invitation)> SeedAsync(
        KycRemoteInvitationChannel channel = KycRemoteInvitationChannel.Email)
    {
        var file = KycFile.Open(
            _tenantId, _customerId, KycChannel.LeadConversion, Guid.NewGuid(), TimeProvider.System,
            agencyId: _agencyId, productLineId: Guid.NewGuid(), responsibleAgentId: Guid.NewGuid());

        var invitation = KycRemoteInvitation.Issue(
            _tenantId, file.Id, _customerId, validityHours: 72, createdBy: Guid.Empty,
            clock: TimeProvider.System, channel: channel);

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

        // The WHOLE payload, pinned as a set rather than key by key: the property being protected
        // is that nothing else is in there, and a per-key assertion passes just as well with a
        // client number sitting beside the three it checked. `items` joined the list with KRM-B-10
        // and is empty on an initial link — the complement wording is the only thing that renders
        // it, and this one is not a complement.
        data.Keys.Should().BeEquivalentTo(["link", "expires_at", "agency_name", "items"]);
        data["agency_name"].Should().Be("Abidjan Plateau");
        data["items"].Should().BeEquivalentTo(Array.Empty<string>());
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

    // ── Le canal SMS ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_link_marked_for_SMS_is_TEXTED_and_never_mailed()
    {
        var (_, invitation) = await SeedAsync(KycRemoteInvitationChannel.Sms);

        await Job().ExecuteAsync(invitation.Id, _tenantId);

        _texted.Should().ContainSingle();

        // L'inverse compte autant : un canal choisi qui part quand même par e-mail est une
        // promesse rompue que personne ne voit, le client ayant bien reçu quelque chose.
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task The_text_message_carries_the_link_and_names_NOBODY()
    {
        var (_, invitation) = await SeedAsync(KycRemoteInvitationChannel.Sms);

        await Job().ExecuteAsync(invitation.Id, _tenantId);

        var sms = _texted.Single();
        var data = sms.TemplateData!;

        data["link"].ToString().Should().Contain("/kyc/verification#t=");
        data["hours"].Should().Be(72);

        // Un texto se lit sur un écran verrouillé : ni l'agence, ni la liste des pièces, ni quoi
        // que ce soit qui désigne l'établissement ou la personne à côté du porteur de créance.
        data.Should().NotContainKey("agency_name");
        data.Should().NotContainKey("items");
        data.Should().NotContainKey("expires_at");
    }

    [Fact]
    public async Task The_text_message_is_keyed_on_the_invitation_so_a_re_issue_is_not_silenced()
    {
        var (_, invitation) = await SeedAsync(KycRemoteInvitationChannel.Sms);

        await Job().ExecuteAsync(invitation.Id, _tenantId);

        _texted.Single().IdempotencyKey.Should().Contain(invitation.Id.ToString());
    }

    [Fact]
    public async Task A_text_that_cannot_be_queued_still_leaves_a_working_link()
    {
        _customers.NotifyClientBySmsAsync(
                Arg.Any<NotifyClientBySmsRequest>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var (_, invitation) = await SeedAsync(KycRemoteInvitationChannel.Sms);

        await Job().ExecuteAsync(invitation.Id, _tenantId);

        var stored = await _db.KycRemoteInvitations.SingleAsync(
            i => i.Id == invitation.Id, CancellationToken.None);

        stored.Status.Should().Be(KycRemoteInvitationStatus.Sent);
        stored.TokenHash.Should().NotBeNull(
            "le lien fonctionne : seul l'acheminement a échoué, et le défaire rendrait inutilisable "
            + "un lien qui a peut-être été reçu");
    }
}

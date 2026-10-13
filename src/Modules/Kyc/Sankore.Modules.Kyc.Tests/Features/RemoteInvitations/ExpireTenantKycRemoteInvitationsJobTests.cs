namespace Sankore.Modules.Kyc.Tests.Features.RemoteInvitations;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.RemoteInvitations.Expiry;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Xunit;

/// <summary>
/// KRM-B-04: the nightly sweep closes lapsed links and tells the agent who converted the customer.
///
/// <para>
/// Nothing here is a security control — <c>KycRemoteInvitation.IsUsableAt</c> compares the clock,
/// so a link dies the moment its window closes whether or not a job runs. The sweep makes the dead
/// link VISIBLE, and these cases are about that: the right rows move, the wrong ones do not, and
/// the agent is told once.
/// </para>
/// </summary>
public sealed class ExpireTenantKycRemoteInvitationsJobTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();

    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly List<NotifyUserRequest> _notified = [];

    public ExpireTenantKycRemoteInvitationsJobTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();

        _administration
            .NotifyUserAsync(Arg.Do<NotifyUserRequest>(_notified.Add), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    private ExpireTenantKycRemoteInvitationsJob Job()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton(_administration);
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();

        return new ExpireTenantKycRemoteInvitationsJob(
            new FixedScopeFactory(services.BuildServiceProvider()));
    }

    /// <param name="issuedDaysAgo">
    /// Drives the window: the link is issued in the past with a one-hour lifetime, so a positive
    /// value is a link that has lapsed.
    /// </param>
    private async Task<KycRemoteInvitation> SeedAsync(
        int issuedDaysAgo, Guid? responsibleAgentId = null, int validityHours = 1)
    {
        var file = KycFile.Open(
            _tenantId, Guid.NewGuid(), KycChannel.LeadConversion, _agentId, TimeProvider.System,
            productLineId: Guid.NewGuid(),
            responsibleAgentId: responsibleAgentId ?? _agentId);

        var invitation = KycRemoteInvitation.Issue(
            _tenantId, file.Id, file.CustomerId, validityHours, Guid.Empty,
            new FrozenClock(DateTimeOffset.UtcNow.AddDays(-issuedDaysAgo)));

        _db.KycFiles.Add(file);
        _db.KycRemoteInvitations.Add(invitation);
        await _db.SaveChangesAsync(CancellationToken.None);

        return invitation;
    }

    /// <summary>
    /// <c>IgnoreQueryFilters</c> because one case asserts about ANOTHER tenant's row, which the
    /// context's global filter would hide — and a helper that answered "not found" there would
    /// make the isolation case pass without observing anything.
    /// </summary>
    private async Task<KycRemoteInvitationStatus> StatusOfAsync(Guid id)
        => (await _db.KycRemoteInvitations.AsNoTracking().IgnoreQueryFilters()
                .FirstAsync(i => i.Id == id, CancellationToken.None)).Status;

    [Fact]
    public async Task A_lapsed_link_is_closed_and_the_responsible_agent_is_told()
    {
        var invitation = await SeedAsync(issuedDaysAgo: 3);

        await Job().ExecuteAsync(_tenantId);

        (await StatusOfAsync(invitation.Id)).Should().Be(KycRemoteInvitationStatus.Expired);

        var mail = _notified.Should().ContainSingle().Subject;
        mail.UserId.Should().Be(_agentId);
        mail.TemplateKey.Should().Be(ExpireTenantKycRemoteInvitationsJob.TemplateKey);
    }

    [Fact]
    public async Task The_notification_names_no_customer()
    {
        // A rendered body sits in a mail provider's delivery log. The agent opens the file and the
        // file has everything; "who is being KYC'd" is not worth putting there to save a click.
        var invitation = await SeedAsync(issuedDaysAgo: 3);

        await Job().ExecuteAsync(_tenantId);

        var data = _notified.Should().ContainSingle().Subject.TemplateData!;
        data.Keys.Should().BeEquivalentTo(["kyc_file_id", "expired_at"]);
    }

    [Fact]
    public async Task A_link_still_inside_its_window_is_left_alone()
    {
        var invitation = await SeedAsync(issuedDaysAgo: 0, validityHours: 72);

        await Job().ExecuteAsync(_tenantId);

        (await StatusOfAsync(invitation.Id)).Should().Be(KycRemoteInvitationStatus.Scheduled);
        _notified.Should().BeEmpty();
    }

    [Fact]
    public async Task An_already_revoked_link_is_not_re_closed()
    {
        // Revoked is a final state and it says WHY. Overwriting it with Expired would lose the
        // motive an agent is reading.
        var invitation = await SeedAsync(issuedDaysAgo: 3);

        var tracked = await _db.KycRemoteInvitations
            .AsTracking()
            .FirstAsync(i => i.Id == invitation.Id, CancellationToken.None);
        tracked.Revoke("Remplacé par un nouveau lien", null, TimeProvider.System);
        await _db.SaveChangesAsync(CancellationToken.None);

        await Job().ExecuteAsync(_tenantId);

        (await StatusOfAsync(invitation.Id)).Should().Be(KycRemoteInvitationStatus.Revoked);
        _notified.Should().BeEmpty();
    }

    [Fact]
    public async Task A_second_sweep_does_not_mail_the_agent_again()
    {
        // A retry, or a redeployment mid-run. The idempotency key is the invitation's.
        await SeedAsync(issuedDaysAgo: 3);

        await Job().ExecuteAsync(_tenantId);
        var first = _notified.Count;

        await Job().ExecuteAsync(_tenantId);

        _notified.Should().HaveCount(first);
    }

    [Fact]
    public async Task A_file_with_no_responsible_agent_is_closed_without_a_notification()
    {
        // A file opened at a counter, or one predating KRM-B-02. Nothing to notify, and nothing
        // wrong: the panel on the customer's file shows the expired link to whoever opens it.
        var invitation = await SeedAsync(issuedDaysAgo: 3, responsibleAgentId: Guid.Empty);

        await Job().ExecuteAsync(_tenantId);

        (await StatusOfAsync(invitation.Id)).Should().Be(KycRemoteInvitationStatus.Expired);
        _notified.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_tenants_links_are_untouched()
    {
        var mine = await SeedAsync(issuedDaysAgo: 3);

        var otherTenant = Guid.NewGuid();
        var theirs = KycRemoteInvitation.Issue(
            otherTenant, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.Empty,
            new FrozenClock(DateTimeOffset.UtcNow.AddDays(-3)));

        _db.KycRemoteInvitations.Add(theirs);
        await _db.SaveChangesAsync(CancellationToken.None);

        await Job().ExecuteAsync(_tenantId);

        (await StatusOfAsync(mine.Id)).Should().Be(KycRemoteInvitationStatus.Expired);
        (await StatusOfAsync(theirs.Id)).Should().Be(KycRemoteInvitationStatus.Scheduled);
    }

    [Fact]
    public async Task The_system_placeholder_tenant_is_a_no_op()
    {
        await SeedAsync(issuedDaysAgo: 3);

        await Job().ExecuteAsync(Guid.Empty);

        _notified.Should().BeEmpty();
    }

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

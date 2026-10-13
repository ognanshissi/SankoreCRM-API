namespace Sankore.Modules.Kyc.Tests.Features.PublicRemote;

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.PublicRemote;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.RemoteLinks;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The gate every anonymous call goes through (KRM-B-05, §4.3, §8).
///
/// <para>
/// This is the module's only unauthenticated write surface, so the cases here are the ones that
/// would be exploits rather than bugs: a token walked on another institution's domain, a link whose
/// window has closed but whose nightly sweep has not run, a session whose link was revoked while
/// the customer was uploading.
/// </para>
/// </summary>
public sealed class KycRemoteGateTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly IKycRemoteLinkTokens _tokens = new KycRemoteLinkTokens();
    private readonly ITenantStore _tenants = Substitute.For<ITenantStore>();
    private readonly IKycRemoteSessions _sessions = Substitute.For<IKycRemoteSessions>();
    private readonly DefaultHttpContext _http = new();

    private const string MyFqdn = "imf.sankore.app";
    private const string OtherFqdn = "rival.sankore.app";

    public KycRemoteGateTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();

        _tenants.GetByFqdnAsync(MyFqdn, Arg.Any<CancellationToken>())
            .Returns(Tenant(_tenantId, MyFqdn));
        _tenants.GetByFqdnAsync(OtherFqdn, Arg.Any<CancellationToken>())
            .Returns(Tenant(_otherTenantId, OtherFqdn));
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    private static TenantInfo Tenant(Guid id, string fqdn)
        => new(id, "Institution", fqdn, true, false, null, null, "fr");

    private KycRemoteGate Gate(TimeProvider? clock = null)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(_http);

        return new KycRemoteGate(
            _db, _tokens, _sessions, _tenants, accessor,
            clock ?? TimeProvider.System,
            NullLogger<KycRemoteGate>.Instance);
    }

    /// <summary>Seeds a file and a link that has been sent, and returns the clear token.</summary>
    private async Task<(KycRemoteInvitation Invitation, string Token)> SeedSentLinkAsync(
        Guid? tenantId = null,
        KycFileStatus fileStatus = KycFileStatus.Collecting,
        int validityHours = 72,
        TimeProvider? issuedAt = null)
    {
        var tenant = tenantId ?? _tenantId;
        var clock = issuedAt ?? TimeProvider.System;

        var file = KycFile.Open(
            tenant, Guid.NewGuid(), KycChannel.LeadConversion, Guid.NewGuid(), TimeProvider.System,
            productLineId: Guid.NewGuid(), responsibleAgentId: Guid.NewGuid());

        if (fileStatus == KycFileStatus.Verifying)
            file.SubmitForVerification(Guid.NewGuid(), TimeProvider.System);

        var invitation = KycRemoteInvitation.Issue(
            tenant, file.Id, file.CustomerId, validityHours, Guid.Empty, clock);

        var (token, hash) = _tokens.Mint();
        invitation.AttachTokenAndMarkSent(hash, clock);

        _db.KycFiles.Add(file);
        _db.KycRemoteInvitations.Add(invitation);
        await _db.SaveChangesAsync(CancellationToken.None);

        return (invitation, token);
    }

    // ── Tenant isolation: the case the spec names in its definition of done ──

    [Fact]
    public async Task A_token_presented_on_another_tenants_domain_is_refused()
    {
        var (_, token) = await SeedSentLinkAsync();
        _http.Request.Headers["X-Tenant-Fqdn"] = OtherFqdn;

        var resolution = await Gate().ResolveLinkAsync(token, track: false, CancellationToken.None);

        resolution.State.Should().Be(KycRemoteGate.LinkState.WrongTenant);
        resolution.Invitation.Should().BeNull(
            "nothing about another institution's link may reach the caller, not even its dates");
    }

    [Fact]
    public async Task A_token_presented_on_its_own_domain_is_accepted()
    {
        var (invitation, token) = await SeedSentLinkAsync();
        _http.Request.Headers["X-Tenant-Fqdn"] = MyFqdn;

        var resolution = await Gate().ResolveLinkAsync(token, track: false, CancellationToken.None);

        resolution.IsUsable.Should().BeTrue();
        resolution.Invitation!.Id.Should().Be(invitation.Id);
    }

    [Fact]
    public async Task An_absent_domain_header_does_not_refuse()
    {
        // The token IS the credential. Refusing a request that merely did not say which domain it
        // came from would break every non-browser client — a curl reproduction, a probe, the
        // Angular app before its interceptor is wired.
        var (_, token) = await SeedSentLinkAsync();

        (await Gate().ResolveLinkAsync(token, track: false, CancellationToken.None))
            .IsUsable.Should().BeTrue();
    }

    [Fact]
    public async Task An_unrecognised_domain_does_not_refuse()
    {
        var (_, token) = await SeedSentLinkAsync();
        _http.Request.Headers["X-Tenant-Fqdn"] = "not-hosted-here.example";

        (await Gate().ResolveLinkAsync(token, track: false, CancellationToken.None))
            .IsUsable.Should().BeTrue("a request from somewhere we do not host is not a mismatch");
    }

    // ── The window is read off the clock, not off the status ─────────────────

    [Fact]
    public async Task A_link_past_its_window_is_expired_even_before_the_nightly_sweep()
    {
        // THE case this matters for: the status still says Sent because no job has run, and a
        // bearer credential must not keep working for up to a day past its stated lifetime.
        var (_, token) = await SeedSentLinkAsync(
            validityHours: 1, issuedAt: new FrozenClock(DateTimeOffset.UtcNow.AddDays(-2)));

        var resolution = await Gate().ResolveLinkAsync(token, track: false, CancellationToken.None);

        resolution.State.Should().Be(KycRemoteGate.LinkState.Expired);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    public async Task A_value_that_cannot_be_a_token_is_unknown(string? candidate)
    {
        (await Gate().ResolveLinkAsync(candidate, track: false, CancellationToken.None))
            .State.Should().Be(KycRemoteGate.LinkState.Unknown);
    }

    [Fact]
    public async Task A_token_of_the_right_shape_that_matches_nothing_is_unknown()
    {
        var (unrelated, _) = _tokens.Mint();

        (await Gate().ResolveLinkAsync(unrelated, track: false, CancellationToken.None))
            .State.Should().Be(KycRemoteGate.LinkState.Unknown);
    }

    [Fact]
    public async Task A_revoked_link_is_reported_as_revoked()
    {
        var (invitation, token) = await SeedSentLinkAsync();

        var tracked = await _db.KycRemoteInvitations
            .AsTracking().FirstAsync(i => i.Id == invitation.Id, CancellationToken.None);
        tracked.Revoke("Remplacé par un nouveau lien", null, TimeProvider.System);
        await _db.SaveChangesAsync(CancellationToken.None);

        (await Gate().ResolveLinkAsync(token, track: false, CancellationToken.None))
            .State.Should().Be(KycRemoteGate.LinkState.Revoked);
    }

    [Fact]
    public async Task A_link_whose_file_has_moved_on_is_reported_separately()
    {
        // An agent took over at the counter. The customer must not be told to keep uploading, and
        // must not be told their link was revoked either — nothing was taken from them.
        var (_, token) = await SeedSentLinkAsync(fileStatus: KycFileStatus.Verifying);

        (await Gate().ResolveLinkAsync(token, track: false, CancellationToken.None))
            .State.Should().Be(KycRemoteGate.LinkState.FileClosed);
    }

    // ── Opening is recorded once ──────────────────────────────────────────────

    [Fact]
    public async Task The_first_status_call_records_the_open_and_the_second_does_not_move_it()
    {
        var (invitation, token) = await SeedSentLinkAsync();

        await Gate().ResolveLinkAsync(token, track: true, CancellationToken.None);

        var afterFirst = await _db.KycRemoteInvitations.AsNoTracking()
            .FirstAsync(i => i.Id == invitation.Id, CancellationToken.None);

        afterFirst.Status.Should().Be(KycRemoteInvitationStatus.Opened);
        afterFirst.OpenedAt.Should().NotBeNull();

        await Gate().ResolveLinkAsync(token, track: true, CancellationToken.None);

        var afterSecond = await _db.KycRemoteInvitations.AsNoTracking()
            .FirstAsync(i => i.Id == invitation.Id, CancellationToken.None);

        afterSecond.OpenedAt.Should().Be(afterFirst.OpenedAt,
            "OpenedAt answers 'did the message reach a human and when' — a refresh must not turn it "
            + "into 'last touched'");
    }

    [Fact]
    public async Task Tracking_does_not_walk_an_identified_link_backwards()
    {
        var (invitation, token) = await SeedSentLinkAsync();

        var tracked = await _db.KycRemoteInvitations
            .AsTracking().FirstAsync(i => i.Id == invitation.Id, CancellationToken.None);
        tracked.MarkIdentified(TimeProvider.System);
        await _db.SaveChangesAsync(CancellationToken.None);

        await Gate().ResolveLinkAsync(token, track: true, CancellationToken.None);

        (await _db.KycRemoteInvitations.AsNoTracking()
                .FirstAsync(i => i.Id == invitation.Id, CancellationToken.None))
            .Status.Should().Be(KycRemoteInvitationStatus.Identified,
                "a page refresh must not make the journey appear to un-happen");
    }

    // ── The session is re-checked on every call ──────────────────────────────

    [Fact]
    public async Task A_session_whose_link_was_revoked_mid_journey_stops_working()
    {
        // This is what "the session is revoked" means: a signed JWT cannot be withdrawn, so the
        // invitation is re-read on every call. Without it, an agent revoking a link would leave the
        // customer uploading for up to 20 more minutes.
        var (invitation, _) = await SeedSentLinkAsync();

        var tracked = await _db.KycRemoteInvitations
            .AsTracking().FirstAsync(i => i.Id == invitation.Id, CancellationToken.None);
        tracked.MarkIdentified(TimeProvider.System);
        await _db.SaveChangesAsync(CancellationToken.None);

        _sessions.Current().Returns(new KycRemoteSessionContext(
            invitation.TenantId, invitation.KycFileId, invitation.Id));

        (await Gate().ResolveSessionAsync(CancellationToken.None))
            .IsUsable.Should().BeTrue();

        tracked.Revoke("Adresse email du client modifiée", null, TimeProvider.System);
        await _db.SaveChangesAsync(CancellationToken.None);

        (await Gate().ResolveSessionAsync(CancellationToken.None))
            .State.Should().Be(KycRemoteGate.LinkState.Revoked);
    }

    [Fact]
    public async Task A_submitted_link_cannot_be_used_again_by_its_own_session()
    {
        var (invitation, _) = await SeedSentLinkAsync();

        var tracked = await _db.KycRemoteInvitations
            .AsTracking().FirstAsync(i => i.Id == invitation.Id, CancellationToken.None);
        tracked.MarkIdentified(TimeProvider.System);
        tracked.MarkSubmitted(TimeProvider.System);
        await _db.SaveChangesAsync(CancellationToken.None);

        _sessions.Current().Returns(new KycRemoteSessionContext(
            invitation.TenantId, invitation.KycFileId, invitation.Id));

        (await Gate().ResolveSessionAsync(CancellationToken.None))
            .State.Should().Be(KycRemoteGate.LinkState.Consumed);
    }

    [Fact]
    public async Task A_session_naming_a_file_that_is_not_its_links_is_refused()
    {
        // A forged claim set, or a token reused across files. The three claims are matched
        // TOGETHER, so a session cannot be re-pointed at another file of the same tenant.
        var (invitation, _) = await SeedSentLinkAsync();

        _sessions.Current().Returns(new KycRemoteSessionContext(
            invitation.TenantId, Guid.NewGuid(), invitation.Id));

        (await Gate().ResolveSessionAsync(CancellationToken.None))
            .State.Should().Be(KycRemoteGate.LinkState.Unknown);
    }

    [Fact]
    public async Task A_session_of_another_tenant_is_refused_on_this_domain()
    {
        var (invitation, _) = await SeedSentLinkAsync(tenantId: _otherTenantId);
        _http.Request.Headers["X-Tenant-Fqdn"] = MyFqdn;

        _sessions.Current().Returns(new KycRemoteSessionContext(
            _otherTenantId, invitation.KycFileId, invitation.Id));

        (await Gate().ResolveSessionAsync(CancellationToken.None))
            .State.Should().Be(KycRemoteGate.LinkState.WrongTenant);
    }

    [Fact]
    public async Task No_session_at_all_is_unknown()
    {
        _sessions.Current().Returns((KycRemoteSessionContext?)null);

        (await Gate().ResolveSessionAsync(CancellationToken.None))
            .State.Should().Be(KycRemoteGate.LinkState.Unknown);
    }

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

namespace Sankore.Modules.Kyc.Tests.Features.RemoteInvitations;

using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Files;
using Sankore.Modules.Kyc.Features.RemoteInvitations.IssueInvitation;
using Sankore.Modules.Kyc.Features.RemoteInvitations.ReissueInvitation;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

/// <summary>
/// The agency perimeter on the AGENT path of KRM-B-04.
///
/// <para>
/// These cases exist because the first version of this feature did not have them. The endpoint
/// carried <c>kyc:remote-invite</c> and nothing else, and the command it reached was the one the
/// conversion uses — which runs as SYSTEM and is deliberately unscoped. An agent in one branch
/// holding a file id from another could therefore have the system mail a single-use link into that
/// branch's customer file, and revoke the link that customer was already holding. A permission
/// says which VERBS a caller has; it says nothing about which FILES they have them on.
/// </para>
/// </summary>
public sealed class ReissueKycRemoteInvitationHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _myAgencyId = Guid.NewGuid();
    private readonly Guid _otherAgencyId = Guid.NewGuid();

    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly ISender _sender = Substitute.For<ISender>();

    public ReissueKycRemoteInvitationHandlerTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();

        // The inner command is exercised by its own suite; here it only has to be observable.
        _sender
            .Send(Arg.Any<IssueKycRemoteInvitationCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(new IssueKycRemoteInvitationResult(
                Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(72), Reused: false)));
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    private ReissueKycRemoteInvitationHandler Handler(params Guid[] accessibleAgencies)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Id.Returns(_userId);
        currentUser.TenantId.Returns(_tenantId);

        return new ReissueKycRemoteInvitationHandler(
            new KycFileAccess(_db, currentUser, new FakeAgencyScope(accessibleAgencies)),
            currentUser,
            _sender);
    }

    private async Task<KycFile> SeedFileAsync(Guid? agencyId = null)
    {
        var file = KycFile.Open(
            _tenantId, Guid.NewGuid(), KycChannel.LeadConversion, Guid.NewGuid(),
            TimeProvider.System,
            agencyId: agencyId ?? _myAgencyId,
            productLineId: Guid.NewGuid(),
            responsibleAgentId: Guid.NewGuid());

        _db.KycFiles.Add(file);
        await _db.SaveChangesAsync(CancellationToken.None);
        return file;
    }

    private IReadOnlyList<IssueKycRemoteInvitationCommand> Issued()
        => [.. _sender.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<IssueKycRemoteInvitationCommand>()];

    [Fact]
    public async Task A_file_inside_the_perimeter_is_re_sent()
    {
        var file = await SeedFileAsync();

        var result = await Handler(_myAgencyId).Handle(
            new ReissueKycRemoteInvitationCommand(file.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var issued = Issued().Should().ContainSingle().Subject;
        issued.KycFileId.Should().Be(file.Id);
        issued.IssuedBy.Should().Be(_userId, "the row must record that a human asked for this");
        issued.ReplaceActive.Should().BeTrue("an agent clicks because the first link did not work");
        issued.DelayMinutes.Should().Be(0, "the welcome mail went out days ago");
    }

    [Fact]
    public async Task A_file_in_another_branch_is_refused_as_not_found()
    {
        // THE case this handler exists for.
        var file = await SeedFileAsync(agencyId: _otherAgencyId);

        var result = await Handler(_myAgencyId).Handle(
            new ReissueKycRemoteInvitationCommand(file.Id), CancellationToken.None);

        result.Error.Should().Be(KycErrors.FileNotFound,
            "never FORBIDDEN: a 403 would confirm another branch's file exists");
    }

    [Fact]
    public async Task Nothing_is_issued_and_nothing_is_revoked_when_the_perimeter_refuses()
    {
        // The check runs BEFORE the inner command, so no link is mailed and — the half that would
        // have been worse — the link the other branch's customer is holding is left alone.
        var file = await SeedFileAsync(agencyId: _otherAgencyId);

        var existing = KycRemoteInvitation.Issue(
            _tenantId, file.Id, file.CustomerId, 72, Guid.Empty, TimeProvider.System);
        _db.KycRemoteInvitations.Add(existing);
        await _db.SaveChangesAsync(CancellationToken.None);

        await Handler(_myAgencyId).Handle(
            new ReissueKycRemoteInvitationCommand(file.Id), CancellationToken.None);

        Issued().Should().BeEmpty();

        var untouched = await _db.KycRemoteInvitations.AsNoTracking()
            .FirstAsync(i => i.Id == existing.Id, CancellationToken.None);
        untouched.Status.Should().Be(KycRemoteInvitationStatus.Scheduled);
    }

    [Fact]
    public async Task An_unrestricted_caller_reaches_every_branch()
    {
        var file = await SeedFileAsync(agencyId: _otherAgencyId);

        var result = await Handler().Handle(
            new ReissueKycRemoteInvitationCommand(file.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a null perimeter is the super-user case");
    }

    [Fact]
    public async Task A_file_with_no_agency_is_invisible_to_a_restricted_caller()
    {
        // Failing closed: an agency M01 could not resolve must not become everyone's.
        var file = await SeedFileAsync(agencyId: Guid.Empty);

        (await Handler(_myAgencyId).Handle(
                new ReissueKycRemoteInvitationCommand(file.Id), CancellationToken.None))
            .Error.Should().Be(KycErrors.FileNotFound);

        (await Handler().Handle(
                new ReissueKycRemoteInvitationCommand(file.Id), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_file_is_refused_the_same_way_as_one_out_of_scope()
    {
        // Indistinguishable on purpose — that is what stops the endpoint being a file-id oracle.
        var result = await Handler(_myAgencyId).Handle(
            new ReissueKycRemoteInvitationCommand(Guid.NewGuid()), CancellationToken.None);

        result.Error.Should().Be(KycErrors.FileNotFound);
        Issued().Should().BeEmpty();
    }

    [Fact]
    public async Task Another_tenants_file_is_refused()
    {
        var file = KycFile.Open(
            Guid.NewGuid(), Guid.NewGuid(), KycChannel.LeadConversion, Guid.NewGuid(),
            TimeProvider.System, agencyId: _myAgencyId, productLineId: Guid.NewGuid());

        _db.KycFiles.Add(file);
        await _db.SaveChangesAsync(CancellationToken.None);

        // The context's global query filter is what scopes this read, which is correct: every
        // caller of KycFileAccess is inside an HTTP request. An unrestricted caller of the WRONG
        // tenant must still be refused.
        var result = await Handler().Handle(
            new ReissueKycRemoteInvitationCommand(file.Id), CancellationToken.None);

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
}

namespace Sankore.Shared.Infrastructure.Tests.Behaviors;

using FluentAssertions;
using MediatR;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

public sealed class AgencyAuthorizationBehaviorTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OwnAgencyId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ForeignAgencyId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    // ── Hand-rolled fakes (this project has no NSubstitute) ────────────

    /// <summary>Returns <c>null</c> (unrestricted) when no agency is given.</summary>
    private sealed class FakeAgencyScope(params Guid[] accessible) : IAgencyScopeProvider
    {
        public int Calls { get; private set; }

        public Task<IReadOnlySet<Guid>?> GetAccessibleAgencyIdsAsync(
            Guid tenantId, Guid userId, CancellationToken ct)
        {
            Calls++;
            IReadOnlySet<Guid>? set = accessible.Length == 0 ? null : accessible.ToHashSet();
            return Task.FromResult(set);
        }

        public async Task<bool> CanAccessAgencyAsync(
            Guid tenantId, Guid userId, Guid agencyId, CancellationToken ct)
        {
            var set = await GetAccessibleAgencyIdsAsync(tenantId, userId, ct);
            return set is null || set.Contains(agencyId);
        }
    }

    private sealed class FakeCurrentUser(Guid id, Guid tenantId, bool isAuthenticated) : ICurrentUser
    {
        public Guid Id { get; } = id;
        public Guid TenantId { get; } = tenantId;
        public string DisplayName => isAuthenticated ? "tester" : "anonymous";
        public bool IsAuthenticated { get; } = isAuthenticated;
        public IReadOnlyList<string> Roles => [];
    }

    // ── Requests ───────────────────────────────────────────────────────

    private sealed record PlainCommand : IRequest<Result>;

    private sealed record ScopedCommand(Guid? TargetAgencyId) : IRequest<Result>, IAgencyScopedRequest;

    private sealed record ScopedQuery(Guid? TargetAgencyId) : IRequest<Result<Guid>>, IAgencyScopedRequest;

    private static AgencyAuthorizationBehavior<TRequest, TResponse> Build<TRequest, TResponse>(
        IAgencyScopeProvider scope, bool authenticated = true)
        where TRequest : notnull
        => new(scope, new FakeCurrentUser(UserId, TenantId, authenticated), new FixedTenantContext(TenantId));

    // ── S1: a request that is not agency-scoped is untouched ───────────

    [Fact]
    public async Task Passes_through_a_request_that_is_not_agency_scoped()
    {
        var scope = new FakeAgencyScope(ForeignAgencyId);

        var result = await Build<PlainCommand, Result>(scope).Handle(
            new PlainCommand(),
            () => Task.FromResult(Result.Ok()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        scope.Calls.Should().Be(0, "the perimeter must not be queried for a non-scoped request");
    }

    [Fact]
    public async Task Passes_through_when_the_target_agency_is_null()
    {
        var scope = new FakeAgencyScope(ForeignAgencyId);

        var result = await Build<ScopedCommand, Result>(scope).Handle(
            new ScopedCommand(null),
            () => Task.FromResult(Result.Ok()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        scope.Calls.Should().Be(0);
    }

    // ── S2: super-user (provider returns null) is unrestricted ─────────

    [Fact]
    public async Task Passes_through_for_a_super_user_whose_perimeter_is_unrestricted()
    {
        var scope = new FakeAgencyScope();   // no ids => null => unrestricted

        var result = await Build<ScopedCommand, Result>(scope).Handle(
            new ScopedCommand(ForeignAgencyId),
            () => Task.FromResult(Result.Ok()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        scope.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Passes_through_for_an_agency_inside_the_perimeter()
    {
        var scope = new FakeAgencyScope(OwnAgencyId);

        var result = await Build<ScopedCommand, Result>(scope).Handle(
            new ScopedCommand(OwnAgencyId),
            () => Task.FromResult(Result.Ok()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    // ── S3: out of perimeter → AGENCY_OUT_OF_SCOPE (Result) ────────────

    [Fact]
    public async Task Fails_with_agency_out_of_scope_on_a_non_generic_result()
    {
        var scope = new FakeAgencyScope(OwnAgencyId);
        var handlerRan = false;

        var result = await Build<ScopedCommand, Result>(scope).Handle(
            new ScopedCommand(ForeignAgencyId),
            () => { handlerRan = true; return Task.FromResult(Result.Ok()); },
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("AGENCY_OUT_OF_SCOPE");
        handlerRan.Should().BeFalse("the handler must never run outside the perimeter");
    }

    // ── S4: out of perimeter → AGENCY_OUT_OF_SCOPE (Result<Guid>) ──────

    [Fact]
    public async Task Fails_with_agency_out_of_scope_on_a_generic_result()
    {
        var scope = new FakeAgencyScope(OwnAgencyId);
        var handlerRan = false;

        var result = await Build<ScopedQuery, Result<Guid>>(scope).Handle(
            new ScopedQuery(ForeignAgencyId),
            () => { handlerRan = true; return Task.FromResult(Result<Guid>.Ok(Guid.NewGuid())); },
            CancellationToken.None);

        result.Should().BeOfType<Result<Guid>>();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("AGENCY_OUT_OF_SCOPE");
        handlerRan.Should().BeFalse();
    }

    // ── S5: background jobs (no authenticated user) are never blocked ──

    [Fact]
    public async Task Passes_through_for_an_unauthenticated_background_caller()
    {
        var scope = new FakeAgencyScope(OwnAgencyId);

        var result = await Build<ScopedCommand, Result>(scope, authenticated: false).Handle(
            new ScopedCommand(ForeignAgencyId),
            () => Task.FromResult(Result.Ok()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        scope.Calls.Should().Be(0, "SYSTEM callers are not perimeter-restricted");
    }
}

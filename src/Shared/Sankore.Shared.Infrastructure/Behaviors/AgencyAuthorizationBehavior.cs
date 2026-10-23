namespace Sankore.Shared.Infrastructure.Behaviors;

using MediatR;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Enforces the agency perimeter for every request that declares the agency it
/// targets through <see cref="IAgencyScopedRequest"/>. A user may only act on
/// their own agency, its descendants, and the agencies explicitly delegated to
/// them through a PermissionAttribution (see <see cref="IAgencyScopeProvider"/>).
///
/// Like <see cref="TransactionBehavior{TRequest,TResponse}"/> and
/// <see cref="AuditBehavior{TRequest,TResponse}"/>, the behavior is a no-op for
/// requests it does not apply to, so it can sit in the global pipeline:
/// <list type="bullet">
/// <item>the request does not implement <see cref="IAgencyScopedRequest"/>;</item>
/// <item><c>TargetAgencyId</c> is null (nothing to check);</item>
/// <item>there is no authenticated user — Hangfire jobs and MassTransit
///       consumers run as SYSTEM and are never perimeter-restricted.</item>
/// </list>
/// Otherwise an out-of-perimeter request fails with <c>AGENCY_OUT_OF_SCOPE</c>.
/// Note that single-entity READ slices must keep returning their own NOT_FOUND
/// code instead of relying on this behavior, so the perimeter never leaks the
/// existence of a record.
/// </summary>
public sealed class AgencyAuthorizationBehavior<TRequest, TResponse>(
    IAgencyScopeProvider scope,
    ICurrentUser currentUser,
    ITenantContext tenant)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private const string OutOfScope = "AGENCY_OUT_OF_SCOPE";

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IAgencyScopedRequest scoped
            || scoped.TargetAgencyId is not { } targetAgencyId
            || !currentUser.IsAuthenticated)
        {
            return await next();
        }

        var allowed = await scope.CanAccessAgencyAsync(
            tenant.CurrentTenantId, currentUser.Id, targetAgencyId, cancellationToken);

        if (allowed)
            return await next();

        return BuildFailure();
    }

    private static TResponse BuildFailure()
        => ResultFailures.Of<TResponse>(OutOfScope, "Client.Agency.OutOfScope");
}

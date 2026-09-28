namespace Sankore.Shared.Infrastructure.Behaviors;

using System.Collections.Concurrent;
using System.Reflection;
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

    /// <summary>
    /// Cached <c>Result&lt;T&gt;.Fail(string)</c> per closed response type —
    /// reflection is only paid once per TResponse, not once per request.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, MethodInfo> FailFactories = new();

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
    {
        var responseType = typeof(TResponse);

        if (responseType == typeof(Result))
            return (TResponse)(object)Result.Fail(OutOfScope);

        if (responseType.IsGenericType
            && responseType.GetGenericTypeDefinition() == typeof(Result<>))
        {
            var factory = FailFactories.GetOrAdd(responseType, static t =>
                t.GetMethod(nameof(Result<object>.Fail), BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly,
                    [typeof(string)])
                ?? throw new InvalidOperationException(
                    $"Result<T>.Fail(string) not found on {t.Name}."));

            return (TResponse)factory.Invoke(null, [OutOfScope])!;
        }

        // A handler whose response is not a Result cannot carry the error code,
        // so surface it as a domain error the exception handler will translate.
        throw new DomainException(OutOfScope, "Client.Agency.OutOfScope");
    }
}

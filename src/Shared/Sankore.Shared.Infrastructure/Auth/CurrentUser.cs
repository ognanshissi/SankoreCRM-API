using System.Security.Claims;
using Sankore.Shared.Kernel.Models;

namespace Sankore.Shared.Infrastructure.Auth;

using Microsoft.AspNetCore.Http;
using Sankore.Shared.Kernel;

/// <summary>Read-only view of the authenticated caller, used by audit and domain logic.</summary>
public interface ICurrentUser
{
    Guid Id { get; }

    /// <summary>The tenant of the DATA the caller works on — the test twin in « Mode test ».</summary>
    Guid TenantId { get; }

    /// <summary>
    /// The tenant the caller's account belongs to (sandbox lot 2): the live tenant, in either mode.
    /// Read it to find users, roles and agencies; never to stamp a row of data.
    /// </summary>
    Guid TeamTenantId => TenantTwins.TeamOf(TenantId);

    string DisplayName { get; }
    bool IsAuthenticated { get; }

    /// <summary>Role names from the JWT ClaimTypes.Role claims.</summary>
    IReadOnlyList<string> Roles { get; }

    /// <summary>
    /// L'administrateur qui agit sous cette identité, ou null hors usurpation. Membre par défaut :
    /// les identités de tâches de fond et les doubles de test n'usurpent jamais personne.
    /// </summary>
    Guid? ImpersonatorId => null;

    /// <summary>
    /// Permission codes from the JWT <c>permission</c> claims. Empty by default: a background
    /// identity carries the System role instead, and a test double that needs one says so.
    /// </summary>
    IReadOnlyList<string> Permissions => [];
}

/// <summary>
/// Resolves the current user from the ASP.NET Core HttpContext claims
/// principal (populated by JWT bearer authentication). Registered as
/// Scoped so it is resolved once per HTTP request / message.
/// </summary>
public sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private System.Security.Claims.ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public Guid Id => IsAuthenticated
        ? Guid.Parse(Principal!.FindFirst(ClaimTypes.NameIdentifier)!.Value)
        : Guid.Empty;

    public Guid TenantId => IsAuthenticated
        ? Guid.Parse(Principal!.FindFirst("tenant_id")!.Value)
        : Guid.Empty;

    public Guid TeamTenantId
        => Guid.TryParse(Principal?.FindFirst(TestModeClaims.TeamTenantId)?.Value, out var team)
            ? team
            : TenantTwins.TeamOf(TenantId);

    public string DisplayName => Principal?.FindFirst("name")?.Value ?? "anonymous";

    public IReadOnlyList<string> Roles => Principal?
        .FindAll(ClaimTypes.Role)
        .Select(c => c.Value)
        .ToList() ?? [];

    public Guid? ImpersonatorId => ImpersonationClaims.ReadImpersonatorId(Principal);

    public IReadOnlyList<string> Permissions => Principal?
        .FindAll("permission")
        .Select(c => c.Value)
        .ToList() ?? [];
}

/// <summary>
/// Bridges ICurrentUser to the Kernel's ITenantContext abstraction so every
/// module's DbContext can apply its multi-tenant global query filter without
/// depending on ASP.NET Core at all.
///
/// Resolution order (mirrors TenantResolutionMiddleware):
///   1. JWT claim "tenant_id"        — signed, authoritative.
///   2. HttpContext.Items key         — set by TenantResolutionMiddleware after
///                                      FQDN lookup; avoids a second store call.
///
/// x-tenant-id header is intentionally NOT read here. It is rejected upstream
/// by TenantResolutionMiddleware before this context is ever consulted.
/// </summary>
public sealed class HttpTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    private Guid? Resolve()
    {
        var ctx = accessor.HttpContext;
        if (ctx is null) return null;

        // 1. JWT claim
        var claim = ctx.User.FindFirst("tenant_id")?.Value;
        if (claim is not null && Guid.TryParse(claim, out var fromJwt))
            return fromJwt;

        // 2. Middleware-resolved via FQDN / X-Tenant-Id (stored in Items by TenantResolutionMiddleware)
        if (ctx.Items.TryGetValue(TenantKey.ResolvedTenantKey, out var item)
            && item is Guid fromItems)
            return fromItems;

        return null;
    }

    public bool HasTenant => Resolve() is not null;

    public Guid CurrentTenantId => Resolve()
        ?? throw new InvalidOperationException("No tenant resolved for the current context.");

    /// <summary>
    /// A « Mode test » token carries its live tenant beside the twin (<see cref="TestModeClaims"/>),
    /// so a request never depends on the process having already seen the twin.
    /// </summary>
    public Guid TeamTenantId
        => Guid.TryParse(accessor.HttpContext?.User.FindFirst(TestModeClaims.TeamTenantId)?.Value, out var team)
            ? team
            : TenantTwins.TeamOf(CurrentTenantId);
}

namespace Sankore.Shared.Infrastructure.Auth;

using Microsoft.AspNetCore.Http;
using Sankore.Shared.Kernel;

/// <summary>Read-only view of the authenticated caller, used by audit and domain logic.</summary>
public interface ICurrentUser
{
    Guid Id { get; }
    Guid TenantId { get; }
    string DisplayName { get; }
    bool IsAuthenticated { get; }
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
        ? Guid.Parse(Principal!.FindFirst("sub")!.Value)
        : Guid.Empty;

    public Guid TenantId => IsAuthenticated
        ? Guid.Parse(Principal!.FindFirst("tenant_id")!.Value)
        : Guid.Empty;

    public string DisplayName => Principal?.FindFirst("name")?.Value ?? "anonymous";
}

/// <summary>
/// Bridges ICurrentUser to the Kernel's ITenantContext abstraction so every
/// module's DbContext can apply its multi-tenant global query filter without
/// depending on ASP.NET Core at all.
/// Resolves tenant in priority order:
///   1. JWT claim "tenant_id"
///   2. Request header "x-tenant-id"
/// </summary>
public sealed class HttpTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    private string? Resolve()
    {
        var ctx = accessor.HttpContext;
        if (ctx is null) return null;
        return ctx.User.FindFirst("tenant_id")?.Value
               ?? ctx.Request.Headers["x-tenant-id"].FirstOrDefault();
    }

    public bool HasTenant => Resolve() is not null;

    public Guid CurrentTenantId => Resolve() is { } raw && Guid.TryParse(raw, out var id)
        ? id
        : throw new InvalidOperationException("No tenant resolved for the current context.");
}

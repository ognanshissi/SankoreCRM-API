namespace Sankore.Shared.Infrastructure.Extensions;

using System.Security.Claims;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Reads the tenant_id claim set by the JWT bearer token. Used directly
    /// inside Minimal API endpoint delegates where injecting ICurrentUser
    /// would be overkill for a single field read.
    /// </summary>
    public static Guid GetTenantId(this ClaimsPrincipal user)
    {
        var claim = user.FindFirst("tenant_id")
            ?? throw new InvalidOperationException("Missing tenant_id claim on the authenticated principal.");

        return Guid.Parse(claim.Value);
    }

    /// <summary>
    /// Reads the user id from the token's subject. JwtBearer maps the inbound "sub"
    /// claim to <see cref="ClaimTypes.NameIdentifier"/>, so the mapped type is checked
    /// first and the raw claim only as a fallback (for tokens read with mapping off).
    /// <see cref="Auth.HttpContextCurrentUser"/> reads the same mapped claim.
    /// </summary>
    public static Guid GetUserId(this ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier)
            ?? user.FindFirst("sub")
            ?? throw new InvalidOperationException("Missing sub claim on the authenticated principal.");

        return Guid.Parse(claim.Value);
    }
}

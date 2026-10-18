using System.Text;

namespace Sankore.Admin.Infrastructure.Auth;

/// <summary>
/// Signing and lifetime settings of the platform operators' tokens, bound from <c>AdminJwt</c>.
///
/// <para>
/// Its own section and its own key, never <c>Jwt:*</c> from Sankore.Api: a token minted for a
/// tenant user must not open the registry, and a leaked tenant signing key must not mint an
/// operator. The audience differs too, so even a key reused by mistake is not enough.
/// </para>
/// </summary>
public sealed class AdminJwtOptions
{
    public const string Section = "AdminJwt";

    public string SigningKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;

    /// <summary>Short on purpose: the refresh token carries the session, the access token only a request.</summary>
    public int AccessTokenMinutes { get; set; } = 30;

    public int RefreshTokenTtlDays { get; set; } = 7;

    public bool Validate(out string? error)
    {
        error = null;
        if (Encoding.UTF8.GetByteCount(SigningKey) < 32)
            error = $"{Section}:SigningKey must be at least 32 bytes (HMAC-SHA256).";
        else if (string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience))
            error = $"{Section}:Issuer and {Section}:Audience are required.";
        else if (AccessTokenMinutes <= 0 || RefreshTokenTtlDays <= 0)
            error = $"{Section}:AccessTokenMinutes and {Section}:RefreshTokenTtlDays must be positive.";
        return error is null;
    }
}

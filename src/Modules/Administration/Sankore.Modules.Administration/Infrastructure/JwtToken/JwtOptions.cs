namespace Sankore.Modules.Administration.Infrastructure.JwtToken;

public sealed class JwtOptions
{
    public string SigningKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;

    /// <summary>How long a refresh token is valid. Defaults to 7 days.</summary>
    public int RefreshTokenTtlDays { get; set; } = 7;
}
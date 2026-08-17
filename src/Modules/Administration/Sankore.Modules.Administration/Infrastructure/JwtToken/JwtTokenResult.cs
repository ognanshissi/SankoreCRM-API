namespace Sankore.Modules.Administration.Infrastructure.JwtToken;

public class JwtTokenResult
{
    public string Token { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
}
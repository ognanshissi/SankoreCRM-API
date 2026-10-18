using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Sankore.Admin.Domain;

namespace Sankore.Admin.Infrastructure.Auth;

/// <summary>What a login or a refresh hands back. The refresh token is in clear here and nowhere else.</summary>
public sealed record AdminAuthTokens(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

public static class AdminClaims
{
    /// <summary>Read as is: inbound claim mapping is off, so <c>sub</c> is not renamed to NameIdentifier.</summary>
    public const string Subject = JwtRegisteredClaimNames.Sub;

    public static Guid? AdminId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst(Subject)?.Value, out var id) ? id : null;
}

internal sealed class AdminTokenService(IOptions<AdminJwtOptions> options)
{
    /// <summary>Issues the pair and returns the refresh entity the caller must persist in the same save.</summary>
    public (AdminAuthTokens Tokens, PlatformAdminRefreshToken Refresh) Issue(PlatformAdmin admin)
    {
        var jwt = options.Value;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(jwt.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, admin.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, admin.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("name", admin.FullName),
        };

        var token = new JwtSecurityToken(
            issuer: jwt.Issuer,
            audience: jwt.Audience,
            claims: claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                SecurityAlgorithms.HmacSha256));

        var (refresh, clearRefresh) = PlatformAdminRefreshToken.Issue(
            admin.Id, TimeSpan.FromDays(jwt.RefreshTokenTtlDays));

        return (new AdminAuthTokens(
            new JwtSecurityTokenHandler().WriteToken(token),
            expiresAt,
            clearRefresh,
            refresh.ExpiresAt), refresh);
    }
}

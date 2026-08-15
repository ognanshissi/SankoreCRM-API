using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Sankore.Modules.Users.Domain;

namespace Sankore.Modules.Users.Infrastructure.JwtToken;

/// <summary>
/// Issues signed JWT access tokens. Emits the claims the rest of the system
/// expects: sub, email, name, tenant_id, permission (one per code), mfa_verified.
/// </summary>
internal sealed class JwtTokenService(IOptions<JwtOptions> option): IJwtTokenService
{
    public JwtTokenResult CreateToken(
        AppUser user,
        IList<string> roles,
        IList<string> permissionCodes)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(option.Value.SigningKey!));
        var expiresAt = DateTimeOffset.UtcNow.AddHours(8);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email!),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()), // user session id
            new("name", user.FullName),
            new("tenant_id", user.TenantId.ToString()),
            new("mfa_verified", "false"),
        };

        // Role claims — consumed by RequireRole() policies (e.g. Leads.Reassign)
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        // Fine-grained permission claims — consumed by RequireClaim("permission", …) policies
        claims.AddRange(permissionCodes.Select(p => new Claim("permission", p)));

        var token = new JwtSecurityToken(
            issuer: option.Value.Issuer,
            audience: option.Value.Audience,
            claims: claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));


        return new JwtTokenResult
        {
            Token = new JwtSecurityTokenHandler().WriteToken(token),
            ExpiresAt = expiresAt,
        };
    }
}

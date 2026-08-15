using Microsoft.Extensions.Configuration;
using Sankore.Modules.Users.Domain;

namespace Sankore.Modules.Users.Infrastructure.JwtToken;

internal interface IJwtTokenService
{
    JwtTokenResult CreateToken(AppUser user,
        IList<string> roles,
        IList<string> permissionCodes);
}
using Microsoft.Extensions.Configuration;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.JwtToken;

internal interface IJwtTokenService
{
    JwtTokenResult CreateToken(AppUser user,
        IList<string> roles,
        IList<string> permissionCodes);
}
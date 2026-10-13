using Microsoft.Extensions.Configuration;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.JwtToken;

internal interface IJwtTokenService
{
    JwtTokenResult CreateToken(
        AppUser user,
        IList<string> roles,
        IList<string> permissionCodes,
        string language,
        Guid? sessionId = null,
        ImpersonationGrant? impersonation = null);
}

/// <summary>
/// Ce qui distingue un jeton d'usurpation : qui agit vraiment, et une durée courte et fixe — pas
/// de jeton de rafraîchissement, la session meurt avec ce jeton.
/// </summary>
internal sealed record ImpersonationGrant(Guid ActorId, string ActorName, TimeSpan Lifetime);
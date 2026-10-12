using System.Security.Claims;

namespace Sankore.Shared.Infrastructure.Auth;

/// <summary>
/// Le claim qui porte la session de l'appareil dans le jeton d'accès. Un jeton émis avant les
/// sessions n'en a pas : il reste valable jusqu'à son expiration, comme avant.
/// </summary>
public static class SessionClaim
{
    public const string Type = "sid";

    public static Guid? Read(ClaimsPrincipal? principal)
        => Guid.TryParse(principal?.FindFirst(Type)?.Value, out var id) ? id : null;
}

/// <summary>
/// Répond si une session est encore ouverte. Interrogé à chaque requête authentifiée : une
/// session coupée à distance doit refuser le jeton d'accès déjà émis, pas attendre ses 8 heures.
/// Implémenté par le module Administration, qui possède les sessions.
/// </summary>
public interface ISessionValidator
{
    Task<bool> IsActiveAsync(Guid tenantId, Guid sessionId, CancellationToken ct);
}

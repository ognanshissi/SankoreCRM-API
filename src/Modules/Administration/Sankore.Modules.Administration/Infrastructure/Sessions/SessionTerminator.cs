using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.Sessions;

/// <summary>
/// Ferme des sessions et révoque leurs jetons de rafraîchissement, ensemble. L'un sans l'autre
/// laisse soit un appareil qui se reconnecte tout seul au prochain rafraîchissement, soit une
/// session affichée « active » qui ne peut plus rien. Ne sauvegarde pas : l'appelant décide de sa
/// transaction.
/// </summary>
internal static class SessionTerminator
{
    /// <param name="keepSessionId">Session épargnée — celle de l'appareil qui fait la demande.</param>
    public static async Task<int> EndAsync(
        AdministrationDbContext db, Guid userId, SessionEndReason reason,
        Guid? onlySessionId = null, Guid? keepSessionId = null, CancellationToken ct = default)
    {
        var sessions = await db.UserSessions
            .AsTracking()
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .Where(s => onlySessionId == null || s.Id == onlySessionId)
            .Where(s => keepSessionId == null || s.Id != keepSessionId)
            .ToListAsync(ct);

        var ids = sessions.Select(s => s.Id).ToList();
        foreach (var session in sessions)
        {
            session.End(reason);
            SessionValidator.Forget(session.Id);
        }

        // Tout fermer (ni cible ni session épargnée) emporte aussi les jetons d'avant les sessions,
        // qui n'appartiennent à aucune : c'était le comportement de la déconnexion jusqu'ici.
        var closeAll = onlySessionId is null && keepSessionId is null;
        var tokens = await db.RefreshTokens
            .AsTracking()
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .Where(t => (t.SessionId != null && ids.Contains(t.SessionId.Value)) || (closeAll && t.SessionId == null))
            .ToListAsync(ct);
        foreach (var token in tokens) token.Revoke();

        return sessions.Count;
    }
}

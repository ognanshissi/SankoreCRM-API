using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Sankore.Shared.Infrastructure.Auth;

namespace Sankore.Modules.Administration.Infrastructure.Sessions;

/// <summary>
/// Vérifie à chaque requête que la session du jeton est ouverte, et note au passage son activité.
///
/// Un cache de 30 secondes par session, dans le processus : sans lui, chaque requête de chaque
/// utilisateur coûterait une lecture de plus. Le prix est qu'une session coupée depuis une autre
/// instance de l'API reste acceptée ici jusqu'à 30 secondes — l'instance qui coupe, elle, oublie
/// l'entrée tout de suite (<see cref="Forget"/>).
/// </summary>
internal sealed class SessionValidator(AdministrationDbContext db) : ISessionValidator
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>« Vu il y a moins de 2 minutes » suffit à l'écran : inutile d'écrire plus souvent.</summary>
    private static readonly TimeSpan TouchThreshold = TimeSpan.FromMinutes(2);

    private static readonly ConcurrentDictionary<Guid, (bool Active, DateTimeOffset Until)> Cache = new();

    public static void Forget(Guid sessionId) => Cache.TryRemove(sessionId, out _);

    public async Task<bool> IsActiveAsync(Guid tenantId, Guid sessionId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (Cache.TryGetValue(sessionId, out var cached) && cached.Until > now)
            return cached.Active;

        // Sans filtre de tenant — le contexte n'est pas encore établi — donc avec le prédicat explicite.
        var session = await db.UserSessions
            .IgnoreQueryFilters()
            .Where(s => s.Id == sessionId && s.TenantId == tenantId)
            .Select(s => new { s.RevokedAt, s.LastSeenAt })
            .FirstOrDefaultAsync(ct);

        var active = session is not null && session.RevokedAt is null;
        if (active && now - session!.LastSeenAt > TouchThreshold)
        {
            await db.UserSessions
                .IgnoreQueryFilters()
                .Where(s => s.Id == sessionId && s.TenantId == tenantId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSeenAt, now), ct);
        }

        Cache[sessionId] = (active, now + CacheTtl);
        return active;
    }
}

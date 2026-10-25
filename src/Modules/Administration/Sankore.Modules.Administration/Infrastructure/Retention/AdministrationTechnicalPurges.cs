namespace Sankore.Modules.Administration.Infrastructure.Retention;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Infrastructure.Retention;
using Sankore.Shared.Kernel;

/// <summary>Refresh tokens that can no longer open anything: expired, or revoked.</summary>
internal sealed class DeadRefreshTokensPurge(AdministrationDbContext db) : ITechnicalPurge
{
    public string Name => "administration.refresh_tokens";

    public Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var before = now - TechnicalRetention.DeadTokens;
        return db.RefreshTokens.IgnoreQueryFilters()
            .Where(t => t.ExpiresAt < before || (t.RevokedAt != null && t.RevokedAt < before))
            .ExecuteDeleteAsync(ct);
    }
}

/// <summary>
/// Sessions ended long ago — or idle that long, which no refresh token outlives. Their tokens go
/// first: a token still pointing at a deleted session would be an orphan.
/// </summary>
internal sealed class EndedSessionsPurge(AdministrationDbContext db) : ITechnicalPurge
{
    public string Name => "administration.user_sessions";

    public async Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var before = now - TechnicalRetention.EndedSessions;
        var ended = db.UserSessions.IgnoreQueryFilters()
            .Where(s => (s.RevokedAt != null && s.RevokedAt < before) || s.LastSeenAt < before)
            .Select(s => (Guid?)s.Id);

        await db.RefreshTokens.IgnoreQueryFilters()
            .Where(t => t.SessionId != null && ended.Contains(t.SessionId))
            .ExecuteDeleteAsync(ct);

        return await db.UserSessions.IgnoreQueryFilters()
            .Where(s => (s.RevokedAt != null && s.RevokedAt < before) || s.LastSeenAt < before)
            .ExecuteDeleteAsync(ct);
    }
}

internal sealed class LoginLocationsPurge(AdministrationDbContext db) : ITechnicalPurge
{
    public string Name => "administration.user_login_locations";

    public Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var before = now - TechnicalRetention.LoginLocations;
        return db.UserLoginLocations.IgnoreQueryFilters()
            .Where(l => l.OccuredAt < before)
            .ExecuteDeleteAsync(ct);
    }
}

/// <summary>
/// The file a user import read and the detail of its rejected rows. The job's row stays — who
/// imported what, when, how many — with an empty <c>SourceReference</c> marking the file gone.
/// The processor deletes the file of a successful run already; a failed one kept it forever.
/// </summary>
internal sealed class UserImportFilesPurge(
    AdministrationDbContext db, IFileStore files, ILogger<UserImportFilesPurge> logger) : ITechnicalPurge
{
    public string Name => "administration.user_import_jobs";

    public async Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var before = now - TechnicalRetention.ImportFiles;
        var finished = db.UserImportJobs.IgnoreQueryFilters()
            .Where(j => j.Status != UserImportStatus.Pending && j.Status != UserImportStatus.Processing
                     && (j.CompletedAt ?? j.CreatedAt) < before);

        var withFile = await finished
            .Where(j => j.SourceType == UserImportSourceType.File && j.SourceReference != "")
            .Select(j => j.SourceReference).ToListAsync(ct);
        // A List, not the set: on .NET 10 a set's Contains does not translate.
        var deleted = (await TechnicalFiles.DeleteAsync(files, withFile, logger, ct)).ToList();

        var gone = await finished.Where(j => deleted.Contains(j.SourceReference))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.SourceReference, ""), ct);
        var details = await finished.Where(j => j.FailureDetailsJson != null)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.FailureDetailsJson, (string?)null), ct);
        return gone + details;
    }
}

namespace Sankore.Modules.Leads.Features.LeadSources.Sdk;

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Registers the SDK builds that ship with the host (wwwroot/sdk/&lt;version&gt;/forms.min.js)
/// so the <c>/sdk/v{major}/forms.min.js</c> alias resolves without an admin upload.
/// Runs at startup inside <see cref="LeadsModule.InitializeAsync"/>.
/// Idempotent — a version already in the table is left untouched, and an existing
/// current version for a major is never demoted (an uploaded build stays current).
/// </summary>
internal static class SdkVersionSeeder
{
    public static async Task SeedAsync(
        LeadsDbContext db, ISdkFileStore fileStore, ILogger logger, CancellationToken ct = default)
    {
        var onDisk = fileStore.ListVersions()
            .Select(v => (Version: v, Parsed: System.Version.TryParse(v, out var p) ? p : null))
            .Where(v => v.Parsed is not null)
            // Highest first: within a major, the newest shipped build claims IsCurrent.
            .OrderByDescending(v => v.Parsed)
            .ToList();

        if (onDisk.Count == 0) return;

        var known = await db.SdkVersions
            .IgnoreQueryFilters()
            .Select(v => v.Version)
            .ToListAsync(ct);

        var majorsWithCurrent = await db.SdkVersions
            .IgnoreQueryFilters()
            .Where(v => v.IsCurrent)
            .Select(v => v.Major)
            .ToListAsync(ct);

        foreach (var (version, parsed) in onDisk)
        {
            if (known.Contains(version)) continue;

            var content = await fileStore.ReadAsync(version, "forms.min.js", ct);
            if (content is null) continue;

            var sriHash = $"sha384-{Convert.ToBase64String(SHA384.HashData(content))}";
            var seeded = SdkVersion.Publish(version, parsed!.Major, sriHash);

            // Publish() marks the new row current; only keep that when the major has none yet.
            if (majorsWithCurrent.Contains(parsed.Major))
                seeded.Revoke();
            else
                majorsWithCurrent.Add(parsed.Major);

            db.SdkVersions.Add(seeded);
            logger.LogInformation(
                "Seeded shipped SDK version {Version} (current: {IsCurrent}).",
                version, seeded.IsCurrent);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // ux_sdk_version — another instance seeded the same build concurrently.
            logger.LogWarning(ex, "SDK version seeding skipped — versions already registered.");
        }
    }
}

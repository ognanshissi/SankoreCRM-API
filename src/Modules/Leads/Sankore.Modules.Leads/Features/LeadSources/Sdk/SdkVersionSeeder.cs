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
/// Idempotent — a version already in the table is left untouched.
///
/// <para>
/// Within a major, the HIGHEST registered version is the current one, whether it was shipped
/// here or uploaded by an admin. It used to be "first one wins, never demote", which meant a
/// newer shipped build was registered and then ignored: the alias and every new snippet kept
/// pointing at the older build, so shipping an SDK fix reached nobody. Promotion is only safe
/// because a snippet pins the exact version — see <see cref="SdkVersion.MakeCurrent"/>.
/// </para>
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

        // AsTracking: the context defaults to NoTracking, and PromoteHighestPerMajor MUTATES
        // these rows. Untracked, the promotion computed correctly, logged, and saved nothing.
        var registered = await db.SdkVersions
            .IgnoreQueryFilters()
            .AsTracking()
            .ToListAsync(ct);
        var known = registered.Select(v => v.Version).ToHashSet(StringComparer.Ordinal);

        foreach (var (version, parsed) in onDisk)
        {
            if (known.Contains(version)) continue;

            var content = await fileStore.ReadAsync(version, "forms.min.js", ct);
            if (content is null) continue;

            var sriHash = $"sha384-{Convert.ToBase64String(SHA384.HashData(content))}";
            var seeded = SdkVersion.Publish(version, parsed!.Major, sriHash);

            db.SdkVersions.Add(seeded);
            registered.Add(seeded);
            logger.LogInformation("Seeded shipped SDK version {Version}.", version);
        }

        PromoteHighestPerMajor(registered, logger);

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

    /// <summary>
    /// Exactly one current build per major: the highest version, by semver and not by the order
    /// rows were inserted.
    ///
    /// <para>
    /// Applied to every major on every start-up, not only to rows just seeded, because that is
    /// what repairs a deployment already carrying the old "first one wins" state — otherwise the
    /// newly shipped build would sit in the table, registered and unreachable.
    /// </para>
    /// </summary>
    private static void PromoteHighestPerMajor(List<SdkVersion> registered, ILogger logger)
    {
        foreach (var major in registered.GroupBy(v => v.Major))
        {
            var highest = major
                .OrderByDescending(v => System.Version.TryParse(v.Version, out var p) ? p : new Version(0, 0))
                .First();

            foreach (var version in major.Where(v => v.IsCurrent && v != highest))
                version.Revoke();

            if (highest.IsCurrent) continue;

            highest.MakeCurrent();
            logger.LogInformation(
                "SDK {Version} is now the current build for major {Major}; new snippets pin it.",
                highest.Version, major.Key);
        }
    }
}

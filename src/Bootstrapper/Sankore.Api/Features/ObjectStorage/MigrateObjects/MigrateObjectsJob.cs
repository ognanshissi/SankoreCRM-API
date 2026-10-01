namespace Sankore.Api.Features.ObjectStorage.MigrateObjects;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.ObjectStorage;

/// <summary>
/// Runs the copy, then writes what it actually did to the audit trail.
///
/// <para>
/// Two audit rows exist for one migration on purpose. <c>AuditBehavior</c> records the REQUEST —
/// who asked, for which concern, and whether it was accepted. This job records the OUTCOME, which
/// is the half that matters months later: an operator asking "were the identity documents moved,
/// and did every one of them verify?" cannot answer it from the fact that somebody pressed the
/// button. A log line would have said the same thing and been rotated away.
/// </para>
/// </summary>
public sealed class MigrateObjectsJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>Audited action name. A constant because the audit trail is queried by it.</summary>
    private const string AuditAction = "ObjectStorageMigrationCompleted";

    /// <param name="requestedBy">
    /// The operator who asked, carried through so the outcome row names a person rather than
    /// SYSTEM. Only the display name becomes SYSTEM, as it does for every background job.
    /// </param>
    public async Task ExecuteAsync(string concern, string sourceBasePath, Guid tenantId, Guid requestedBy)
    {
        // Set BEFORE the scope is created: ICurrentUser and ITenantContext are built from this
        // ambient context, and a scope opened first would capture the HTTP implementations and
        // find no request.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, requestedBy, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILogger<MigrateObjectsJob>>();
        var destination = sp.GetRequiredKeyedService<IObjectBackend>(concern);

        var source = new LocalObjectBackend(
            sourceBasePath, sp.GetRequiredService<ILogger<LocalObjectBackend>>());

        var migrator = new ObjectStoreMigrator(sp.GetRequiredService<ILogger<ObjectStoreMigrator>>());

        // The ceiling is the migrator's read limit, not a policy: an object this host wrote is
        // already bounded by its own store's limit, and a volume holding something larger is
        // reported as a failure rather than skipped silently.
        var report = await migrator.MigrateAsync(
            source,
            destination,
            keyPrefix: string.Empty,
            maxBytes: MaxObjectBytes,
            verify: true,

            // Never, from here. See MigrateObjectsCommand: the source is retired by unmounting the
            // volume, after a document has been read back through the API.
            deleteFromSource: false);

        logger.Log(
            report.IsComplete ? LogLevel.Information : LogLevel.Error,
            "Object-storage migration of {Concern} finished: {Copied} copied, {AlreadyPresent} already "
            + "present, {Failed} failed. Source {SourceBasePath} left untouched.",
            concern, report.Copied, report.AlreadyPresent, report.Failures.Count, sourceBasePath);

        await WriteAuditAsync(sp, concern, sourceBasePath, tenantId, requestedBy, report);
    }

    /// <summary>
    /// 1 GiB. Not a tuning knob — it is far above anything either store writes (10 MB for a KYC
    /// image, 64 MiB for an import or export) and exists so a corrupted or foreign file on the
    /// volume is REPORTED as unreadable rather than pulled into memory whole.
    /// </summary>
    private const long MaxObjectBytes = 1024L * 1024 * 1024;

    /// <summary>
    /// How many failing keys the audit row carries. Capped so one corrupted volume cannot put forty
    /// thousand keys into a single row; the count beside it says how many were left out.
    /// </summary>
    private const int MaxAuditedFailures = 50;

    private static async Task WriteAuditAsync(
        IServiceProvider sp, string concern, string sourceBasePath,
        Guid tenantId, Guid requestedBy, ObjectMigrationReport report)
    {
        // The failing keys are included, capped: they are the list an operator needs to decide
        // whether the volume can be retired, and an unbounded list would put a whole corrupted
        // volume into one audit row.
        // PascalCase, like the request row AuditBehavior writes through SanitizedJsonSerializer
        // (default options, so property names as declared). The two rows of one migration are read
        // side by side, and a payload that camel-cases half its keys makes them look unrelated.
        var payload = JsonSerializer.Serialize(new
        {
            Concern = concern,
            SourceBasePath = sourceBasePath,
            report.Copied,
            report.AlreadyPresent,
            Failed = report.Failures.Count,
            Failures = report.Failures.Take(MaxAuditedFailures),
            TruncatedFailures = Math.Max(0, report.Failures.Count - MaxAuditedFailures),
            SourceDeleted = false,
        });

        await sp.GetRequiredService<IAuditWriter>().WriteAsync(
            new AuditEntry(
                Timestamp: DateTimeOffset.UtcNow,
                UserId: requestedBy,
                TenantId: tenantId,
                Action: AuditAction,
                PayloadJson: payload,
                Outcome: report.IsComplete ? "SUCCESS" : "FAILURE",
                ErrorDetail: report.IsComplete
                    ? null
                    : $"{report.Failures.Count} object(s) could not be migrated; the source was kept.",
                ResourceType: "ObjectStorageMigration",
                ResourceId: concern),
            CancellationToken.None);
    }
}

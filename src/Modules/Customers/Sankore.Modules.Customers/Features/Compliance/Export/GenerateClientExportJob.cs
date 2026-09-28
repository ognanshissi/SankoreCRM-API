namespace Sankore.Modules.Customers.Features.Compliance.Export;

using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.SearchClients;
using Sankore.Modules.Customers.Features.Compliance.Retention;
using Sankore.Modules.Customers.Features.Timeline.Projection;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;

/// <summary>
/// Produces the CSV of a queued <see cref="ClientExportJob"/> (US-M01-BE-30).
/// <para>
/// Runs as SYSTEM but exports with the REQUESTER's eyes: the agency perimeter is resolved for
/// <c>ClientExportJob.RequestedBy</c>, never for the SYSTEM account, so an export can never show
/// more than the interactive search would have shown to the person who asked for it.
/// </para>
/// <para>
/// Protected columns are decrypted then masked. The clear value is never written: a CSV leaves
/// the platform and, unlike the reveal endpoint, carries no audit, no rate limit and no expiry.
/// </para>
/// </summary>
public sealed class GenerateClientExportJob(IServiceScopeFactory scopeFactory)
{
    private static readonly JsonSerializerOptions FilterJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Hangfire entry point. Both arguments are opaque identifiers.</summary>
    public async Task ExecuteAsync(Guid exportJobId, Guid tenantId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<CustomersDbContext>();
        var fileStore = sp.GetRequiredService<IFileStore>();
        var agencyScope = sp.GetRequiredService<Sankore.Shared.Kernel.Authorization.IAgencyScopeProvider>();
        var indexer = sp.GetRequiredService<IBlindIndexer>();
        var encryptor = sp.GetRequiredService<IFieldEncryptor>();
        var projector = sp.GetRequiredService<IClientTimelineProjector>();
        var clock = sp.GetRequiredService<TimeProvider>();
        var logger = sp.GetRequiredService<ILogger<GenerateClientExportJob>>();

        var job = await db.ClientExportJobs
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(j => j.TenantId == tenantId && j.Id == exportJobId);

        if (job is null)
        {
            logger.LogError(
                "Client export {ExportId} not found for tenant {TenantId} — nothing to generate.",
                exportJobId, tenantId);
            return;
        }

        // A retried Hangfire attempt must not rebuild a file that is already there.
        if (job.Status is ExportJobStatus.Completed or ExportJobStatus.Failed)
        {
            logger.LogInformation(
                "Client export {ExportId} already {Status} — skipping regeneration.",
                exportJobId, job.Status);
            return;
        }

        job.Start();
        await db.SaveChangesAsync();

        var startedAt = clock.GetUtcNow();

        try
        {
            var filters = JsonSerializer.Deserialize<SearchClientsQuery>(job.FiltersJson, FilterJsonOptions)
                          ?? new SearchClientsQuery(null, null, null, null, null, null, null, null, null);

            var accessible = await agencyScope.GetAccessibleAgencyIdsAsync(
                tenantId, job.RequestedBy, CancellationToken.None);

            var query = ClientExportQueryBuilder.Build(db, tenantId, filters, accessible, indexer);

            // The contact points come along so the primary phone can be masked without a
            // per-client round trip.
            var clients = await query
                .Include(c => c.ContactPoints)
                .Take(ClientExportQueryBuilder.MaxRows)
                .ToListAsync();

            var rows = clients.Select(c => ToRow(c, encryptor)).ToList();

            var content = await ClientExportCsv.WriteAsync(rows, CancellationToken.None);

            using var stream = new MemoryStream(content, writable: false);
            var fileReference = await fileStore.StoreAsync(
                stream, ClientExportCsv.FileName(job.Id), CancellationToken.None);

            var completedAt = clock.GetUtcNow();
            job.Complete(fileReference, rows.Count, completedAt);
            await db.SaveChangesAsync();

            // Structured log — the row count is an acceptance criterion of the US and the only
            // number an auditor can cross-check against the file they were handed.
            logger.LogInformation(
                "Client export {ExportId} for tenant {TenantId} requested by {RequestedBy} completed: "
                + "{RowCount} row(s) written to {FileReference} in {ElapsedMs} ms.",
                job.Id, tenantId, job.RequestedBy, job.RowCount, fileReference,
                (completedAt - startedAt).TotalMilliseconds);

            // Same fact on the timeline, so it survives log rotation. ClientId = Guid.Empty marks
            // a tenant-level entry: an export spans many clients and belongs to no single file.
            // The count is written with thousands separators so the sensitive-value guard does not
            // mistake a long run of digits for a phone number.
            await projector.AppendAsync(
                tenantId: tenantId,
                clientId: Guid.Empty,
                sourceModule: RetentionWindow.TimelineSourceModule,
                entryType: RetentionWindow.ExportCompletedEntryType,
                occurredAt: completedAt,
                summary: $"Client export completed: "
                         + $"{job.RowCount.ToString("N0", CultureInfo.InvariantCulture)} row(s) exported.",
                referenceType: "ClientExportJob",
                referenceId: job.Id.ToString("D"),
                dedupKey: $"{RetentionWindow.TimelineSourceModule}:"
                          + $"{RetentionWindow.ExportCompletedEntryType}:{job.Id:D}",
                ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The message is stored so the requester sees why their export failed; it describes a
            // query or an I/O fault and never carries a client's data.
            logger.LogError(ex, "Client export {ExportId} for tenant {TenantId} failed.",
                exportJobId, tenantId);

            job.Fail(ex.Message, clock.GetUtcNow());
            await db.SaveChangesAsync();
        }
    }

    private static ClientExportRow ToRow(Client client, IFieldEncryptor encryptor)
    {
        // Primary active phone first, then any active phone: a client whose primary flag was
        // never set should still export a contact.
        var phone = client.ContactPoints
            .Where(cp => cp.Type == ContactPointType.Phone && cp.IsActive)
            .OrderByDescending(cp => cp.IsPrimary)
            .ThenBy(cp => cp.CreatedAt)
            .FirstOrDefault();

        var phoneMasked = phone is null
            ? null
            : SensitiveValueMasker.MaskPhone(encryptor.Decrypt(phone.EncryptedValue));

        var documentMasked = SensitiveValueMasker.MaskDocument(
            encryptor.Decrypt(client.EncryptedIdentityDocumentNumber));

        return new ClientExportRow(
            ClientNumber: client.ClientNumber,
            DisplayName: client.DisplayName,
            ClientType: client.Type.ToString(),
            Status: client.Status.ToString(),
            AgencyId: client.AgencyId.ToString("D"),
            AdvisorUserId: client.AdvisorUserId?.ToString("D") ?? string.Empty,
            KycStatus: client.KycStatus.ToString(),
            RiskLevel: client.RiskLevel.ToString(),
            SegmentCode: client.SegmentCode ?? string.Empty,
            PrimaryPhoneMasked: phoneMasked ?? string.Empty,
            IdentityDocumentMasked: documentMasked ?? string.Empty,
            CreatedAt: client.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
    }
}

namespace Sankore.Modules.Leads.Features.Import;

using System.Globalization;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Hangfire background job — thin orchestrator.
/// Reads the uploaded CSV file, parses each row, and dispatches every line
/// through the standard <see cref="CaptureLeadCommand"/> MediatR pipeline
/// (validation, audit, dedup included). Never performs a bulk insert.
/// </summary>
public sealed class ProcessLeadImportJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>Hangfire entry point. Parameters are serialized into the job payload.</summary>
    public async Task ExecuteAsync(Guid importJobId, Guid tenantId, Guid initiatedBy)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, initiatedBy, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<LeadsDbContext>();
        var sender = sp.GetRequiredService<ISender>();
        var fileStore = sp.GetRequiredService<IImportFileStore>();
        var clock = sp.GetRequiredService<TimeProvider>();
        var logger = sp.GetRequiredService<ILogger<ProcessLeadImportJob>>();

        // ── Load import job entity ───────────────────────────────────────
        var importJob = await db.LeadImportJobs
            .AsTracking()
            .FirstOrDefaultAsync(j => j.Id == importJobId);

        if (importJob is null)
        {
            logger.LogError("Import job {ImportJobId} not found", importJobId);
            return;
        }

        importJob.MarkProcessing();
        await db.SaveChangesAsync();

        try
        {
            // ── Parse CSV ────────────────────────────────────────────────
            await using var stream = await fileStore.ReadAsync(importJob.FileReference, CancellationToken.None);
            using var reader = new StreamReader(stream);
            using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HeaderValidated = null,
                MissingFieldFound = null,
                TrimOptions = TrimOptions.Trim,
            });

            var rows = csv.GetRecords<ImportLeadRow>().ToList();

            // ── Dispatch each row through the standard MediatR command ──
            var failures = new List<ImportRowFailure>();
            int succeeded = 0, skipped = 0;

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                try
                {
                    var result = await sender.Send(new CaptureLeadCommand(
                        TenantId:             tenantId,
                        FullName:             row.FullName,
                        PhoneNumber:          row.PhoneNumber,
                        Source:               row.Source,
                        InterestedProduct:    row.InterestedProduct,
                        PreferredLanguage:    row.PreferredLanguage,
                        Latitude:             row.Latitude,
                        Longitude:            row.Longitude,
                        PreferredAgencyId:    null,
                        FirstName:            row.FirstName,
                        LastName:             row.LastName,
                        Email:                row.Email,
                        Gender:               row.Gender,
                        DateOfBirth:          row.DateOfBirth,
                        DesiredAmount:        row.DesiredAmount,
                        DesiredCurrency:      row.DesiredCurrency,
                        Campaign:             row.Campaign,
                        Channel:              row.Channel,
                        Comment:              row.Comment,
                        ExternalReference:    row.ExternalReference,
                        OwnerId:             row.OwnerId,
                        AgencyId:             row.AgencyId));

                    if (!result.IsSuccess)
                    {
                        failures.Add(new ImportRowFailure(i + 1, row.PhoneNumber, result.Error!));
                        continue;
                    }

                    if (result.Value.DuplicateDetected && result.Value.LeadId is null)
                    {
                        skipped++;
                        continue;
                    }

                    succeeded++;
                }
                catch (Exception ex)
                {
                    failures.Add(new ImportRowFailure(i + 1, row.PhoneNumber, ex.Message));
                }
            }

            // ── Finalize ─────────────────────────────────────────────────
            var failureJson = failures.Count > 0
                ? JsonSerializer.Serialize(failures)
                : null;

            importJob.Complete(rows.Count, succeeded, skipped, failures.Count, failureJson, clock);
            await db.SaveChangesAsync();

            logger.LogInformation(
                "Import {ImportJobId} completed: {Succeeded} succeeded, {Skipped} skipped, {Failed} failed out of {Total}",
                importJobId, succeeded, skipped, failures.Count, rows.Count);

            // Clean up the file after successful processing
            await fileStore.DeleteAsync(importJob.FileReference, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import job {ImportJobId} failed", importJobId);
            importJob.Fail(ex.Message, clock);
            await db.SaveChangesAsync();
        }
    }
}

namespace Sankore.Modules.Leads.Features.Import;

using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Modules.Leads.Features.Import.Readers;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Hangfire background job — thin orchestrator.
/// Resolves the reader for the job's source, parses each row, and dispatches it
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

        var sp     = scope.ServiceProvider;
        var db     = sp.GetRequiredService<LeadsDbContext>();
        var sender = sp.GetRequiredService<ISender>();
        var clock  = sp.GetRequiredService<TimeProvider>();
        var logger = sp.GetRequiredService<ILogger<ProcessLeadImportJob>>();

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
            // ── Read raw rows from whichever source this job uses ────────
            var reader = ResolveReader(importJob.SourceType, sp);
            var rows   = await reader.ReadAsync(importJob.SourceReference, CancellationToken.None);

            var defaults = ImportDefaultsSerializer.Deserialize(importJob.DefaultsJson);

            // ── Parse then dispatch each row ─────────────────────────────
            var failures = new List<ImportRowFailure>();
            int succeeded = 0, skipped = 0;

            for (var i = 0; i < rows.Count; i++)
            {
                var raw = rows[i];
                var rowNumber = i + 1;

                try
                {
                    var parsed = LeadRowParser.Parse(raw, defaults, LeadSource.FileImport);

                    if (!parsed.IsValid)
                    {
                        failures.Add(new ImportRowFailure(
                            rowNumber, raw.PhoneNumber ?? "", string.Join(" ", parsed.Errors)));
                        continue;
                    }

                    var row = parsed.Row!;

                    var result = await sender.Send(new CaptureLeadCommand(
                        TenantId:          tenantId,
                        FullName:          row.FullName,
                        PhoneNumber:       row.PhoneNumber,
                        Source:            row.Source,
                        InterestedProduct: row.InterestedProduct,
                        PreferredLanguage: row.PreferredLanguage,
                        Latitude:          row.Latitude,
                        Longitude:         row.Longitude,
                        // The row's agency IS the preferred agency. Passing null here did more
                        // than cost 5 score points: DispatchLeadHandler scopes its candidate
                        // agents with GetAvailableAgentsAsync(tenantId, lead.PreferredAgencyId),
                        // so an imported lead was dispatched across the whole tenant instead of
                        // its own branch.
                        PreferredAgencyId: row.AgencyId,
                        FirstName:         row.FirstName,
                        LastName:          row.LastName,
                        Email:             row.Email,
                        NationalId:        row.NationalId,
                        Gender:            row.Gender,
                        DateOfBirth:       row.DateOfBirth,
                        DesiredAmount:     row.DesiredAmount,
                        DesiredCurrency:   row.DesiredCurrency,
                        Campaign:          row.Campaign,
                        Channel:           row.Channel,
                        Comment:           row.Comment,
                        ExternalReference: row.ExternalReference,
                        OwnerId:           row.OwnerId,
                        AgencyId:          row.AgencyId,
                        CompanyName:       row.CompanyName));

                    if (!result.IsSuccess)
                    {
                        failures.Add(new ImportRowFailure(rowNumber, row.PhoneNumber, result.Error!));
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
                    failures.Add(new ImportRowFailure(rowNumber, raw.PhoneNumber ?? "", ex.Message));
                }
            }

            // ── Finalize ─────────────────────────────────────────────────
            var failureJson = failures.Count > 0
                ? JsonSerializer.Serialize(failures)
                : null;

            importJob.Complete(rows.Count, succeeded, skipped, failures.Count, failureJson, clock);
            await db.SaveChangesAsync();

            logger.LogInformation(
                "Import {ImportJobId} ({SourceType}) completed: {Succeeded} succeeded, {Skipped} skipped, {Failed} failed out of {Total}",
                importJobId, importJob.SourceType, succeeded, skipped, failures.Count, rows.Count);

            // Only file imports leave something behind to clean up.
            if (importJob.SourceType == LeadImportSourceType.File)
            {
                var fileStore = sp.GetRequiredService<IFileStore>();
                await fileStore.DeleteAsync(importJob.SourceReference, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import job {ImportJobId} failed", importJobId);
            importJob.Fail(ex.Message, clock);
            await db.SaveChangesAsync();
        }
    }

    private static ILeadImportSourceReader ResolveReader(
        LeadImportSourceType sourceType, IServiceProvider sp) => sourceType switch
    {
        LeadImportSourceType.File           => sp.GetRequiredService<FileImportReader>(),
        LeadImportSourceType.GoogleSheet    => sp.GetRequiredService<GoogleSheetsImportReader>(),
        LeadImportSourceType.GoogleContacts => sp.GetRequiredService<GoogleContactsImportReader>(),
        _ => throw new ArgumentOutOfRangeException(nameof(sourceType))
    };
}

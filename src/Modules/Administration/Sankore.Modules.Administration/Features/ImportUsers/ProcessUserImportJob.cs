namespace Sankore.Modules.Administration.Features.ImportUsers;

using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.ImportUsers.Readers;
using Sankore.Modules.Administration.Features.Users.CreateUser;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire background job: reads rows from the configured source, dispatches
/// each row through the standard CreateUserCommand pipeline (validation, dedup,
/// role assignment, activation email). Runs under SYSTEM identity.
/// </summary>
public sealed class ProcessUserImportJob(IServiceScopeFactory scopeFactory)
{
    public async Task ExecuteAsync(Guid importJobId, Guid tenantId, Guid initiatedBy)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, initiatedBy, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp     = scope.ServiceProvider;
        var db     = sp.GetRequiredService<AdministrationDbContext>();
        var sender = sp.GetRequiredService<ISender>();
        var clock  = sp.GetRequiredService<TimeProvider>();
        var logger = sp.GetRequiredService<ILogger<ProcessUserImportJob>>();

        var importJob = await db.UserImportJobs
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(j => j.Id == importJobId && j.TenantId == tenantId);

        if (importJob is null)
        {
            logger.LogError("User import job {ImportJobId} not found", importJobId);
            return;
        }

        importJob.MarkProcessing();
        await db.SaveChangesAsync();

        try
        {
            // ── Resolve the right reader for the source type ────────────
            var reader = ResolveReader(importJob.SourceType, sp);
            var rows = await reader.ReadAsync(importJob.SourceReference, CancellationToken.None);

            // ── Resolve agency and role lookups ─────────────────────────
            var agencies = await db.Agencies
                .IgnoreQueryFilters()
                .Where(a => a.TenantId == tenantId && a.IsActive)
                .ToListAsync();

            var roles = await db.Roles.ToListAsync();

            // Default fallbacks
            var defaultAgencyId = agencies.FirstOrDefault()?.Id ?? Guid.Empty;
            var defaultRoleId = roles.FirstOrDefault(r => r.Name == "Agent")?.Id
                             ?? roles.First().Id;

            // ── Process each row ────────────────────────────────────────
            var failures = new List<ImportRowFailure>();
            int succeeded = 0, skipped = 0;

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                try
                {
                    if (string.IsNullOrWhiteSpace(row.Email))
                    {
                        skipped++;
                        continue;
                    }

                    // Resolve agency by code, fallback to default
                    var agencyId = !string.IsNullOrWhiteSpace(row.AgencyCode)
                        ? agencies.FirstOrDefault(a =>
                            a.Name.Equals(row.AgencyCode, StringComparison.OrdinalIgnoreCase))?.Id
                          ?? defaultAgencyId
                        : defaultAgencyId;

                    // Resolve role by code, fallback to default
                    var roleId = !string.IsNullOrWhiteSpace(row.RoleCode)
                        ? roles.FirstOrDefault(r =>
                            r.Name!.Equals(row.RoleCode, StringComparison.OrdinalIgnoreCase))?.Id
                          ?? defaultRoleId
                        : defaultRoleId;

                    var spokenLangs = row.SpokenLanguages?
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList() ?? [row.DefaultLanguage];

                    var specialties = row.Specialties?
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList() ?? [];

                    var result = await sender.Send(new CreateUserCommand(
                        AgencyId:        agencyId,
                        RoleId:          roleId,
                        FirstName:       row.FirstName,
                        LastName:        row.LastName,
                        Email:           row.Email.Trim(),
                        DefaultLanguage: row.DefaultLanguage,
                        SpokenLanguages: spokenLangs,
                        Specialties:     specialties,
                        CallerUserId:    initiatedBy));

                    if (!result.IsSuccess)
                    {
                        failures.Add(new ImportRowFailure(i + 1, row.Email, result.Error!));
                        continue;
                    }

                    succeeded++;
                }
                catch (Exception ex)
                {
                    failures.Add(new ImportRowFailure(i + 1, row.Email, ex.Message));
                }
            }

            var failureJson = failures.Count > 0
                ? JsonSerializer.Serialize(failures)
                : null;

            importJob.Complete(rows.Count, succeeded, skipped, failures.Count, failureJson, clock);
            await db.SaveChangesAsync();

            logger.LogInformation(
                "User import {ImportJobId}: {Succeeded} succeeded, {Skipped} skipped, {Failed} failed / {Total}",
                importJobId, succeeded, skipped, failures.Count, rows.Count);

            // Clean up file if applicable
            if (importJob.SourceType == UserImportSourceType.File)
            {
                var fileStore = sp.GetRequiredService<IUserImportFileStore>();
                await fileStore.DeleteAsync(importJob.SourceReference, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "User import job {ImportJobId} failed", importJobId);
            importJob.Fail(ex.Message, clock);
            await db.SaveChangesAsync();
        }
    }

    private static IUserImportSourceReader ResolveReader(
        UserImportSourceType sourceType, IServiceProvider sp) => sourceType switch
    {
        UserImportSourceType.File            => sp.GetRequiredService<FileImportReader>(),
        UserImportSourceType.GoogleSheet     => sp.GetRequiredService<GoogleSheetsImportReader>(),
        UserImportSourceType.GoogleContacts  => sp.GetRequiredService<GoogleContactsImportReader>(),
        _ => throw new ArgumentOutOfRangeException(nameof(sourceType))
    };
}

public sealed record ImportRowFailure(int RowNumber, string Email, string Error);

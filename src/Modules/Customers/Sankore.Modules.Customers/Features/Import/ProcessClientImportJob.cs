namespace Sankore.Modules.Customers.Features.Import;

using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.CreateIndividualClient;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Features.Import.GetImportStatus;
using Sankore.Modules.Customers.Features.Import.Readers;
using Sankore.Modules.Customers.Features.Import.ValidateImport;
using Sankore.Modules.Customers.Features.LegalEntities.CreateLegalClient;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Hangfire job: reads the rows, then puts each one through the SAME create command an agent
/// would use from the UI. Nothing is inserted directly — the import gets the real duplicate
/// checks, the real encryption, the real client-number allocation and the real audit trail,
/// because it goes through the same door.
///
/// Mirrors <c>ProcessUserImportJob</c> of module M12.
/// </summary>
public sealed class ProcessClientImportJob(IServiceScopeFactory scopeFactory)
{
    public async Task ExecuteAsync(Guid importJobId, Guid tenantId, Guid initiatedBy)
    {
        // The operator who asked for the import stays the actor: the rows are their doing, and
        // an audit trail attributing four hundred clients to SYSTEM helps nobody.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, initiatedBy, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<CustomersDbContext>();
        var sender = sp.GetRequiredService<ISender>();
        var clock = sp.GetRequiredService<TimeProvider>();
        var logger = sp.GetRequiredService<ILogger<ProcessClientImportJob>>();

        var importJob = await db.ClientImportJobs
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(j => j.Id == importJobId && j.TenantId == tenantId);

        if (importJob is null)
        {
            logger.LogError("Client import job {ImportJobId} not found", importJobId);
            return;
        }

        importJob.MarkProcessing();
        await db.SaveChangesAsync();

        try
        {
            var reader = ResolveReader(importJob.SourceType, sp);
            var rows = await reader.ReadAsync(importJob.SourceReference, CancellationToken.None);

            var agencyIdByCode = await db.Clients
                .IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId)
                .Select(c => new { c.AgencyCode, c.AgencyId })
                .Distinct()
                .ToDictionaryAsync(a => a.AgencyCode, a => a.AgencyId, StringComparer.OrdinalIgnoreCase);

            var failures = new List<ClientImportRowFailure>();
            int succeeded = 0, skipped = 0;

            for (var i = 0; i < rows.Count; i++)
            {
                var rowNumber = i + 1;
                var row = rows[i];
                var reference = ReferenceOf(row);

                try
                {
                    var agencyId = ResolveAgency(row, agencyIdByCode, importJob.DefaultAgencyId);
                    if (agencyId is null)
                    {
                        // A client belongs to an agency and nobody can pick one on its behalf.
                        failures.Add(new(rowNumber, reference,
                            "No agency: the row has no AgencyCode and the import has no default agency."));
                        continue;
                    }

                    var outcome = row.IsLegalEntity
                        ? await CreateLegalAsync(sender, row, agencyId.Value)
                        : await CreateIndividualAsync(sender, row, agencyId.Value);

                    switch (outcome.Kind)
                    {
                        case RowOutcome.Created:
                            succeeded++;
                            break;

                        // A phone shared with an existing client is a warning, not a rejection,
                        // at the counter. In bulk it is NOT auto-confirmed: nobody is looking at
                        // the screen, so the row is reported for a human to decide.
                        case RowOutcome.Skipped:
                            skipped++;
                            failures.Add(new(rowNumber, reference, outcome.Reason!));
                            break;

                        default:
                            failures.Add(new(rowNumber, reference, outcome.Reason!));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    // One malformed row must never take the other 399 with it.
                    failures.Add(new(rowNumber, reference, ex.Message));
                }
            }

            var failureJson = failures.Count > 0 ? JsonSerializer.Serialize(failures) : null;

            importJob.Complete(rows.Count, succeeded, skipped, failures.Count, failureJson, clock);
            await db.SaveChangesAsync();

            logger.LogInformation(
                "Client import {ImportJobId}: {Succeeded} created, {Skipped} skipped, {Failed} failed / {Total}",
                importJobId, succeeded, skipped, failures.Count, rows.Count);

            if (importJob.SourceType == ClientImportSourceType.File)
            {
                var fileStore = sp.GetRequiredService<IFileStore>();
                await fileStore.DeleteAsync(importJob.SourceReference, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            // Reading the source failed, or the database did: the whole run is marked failed
            // rather than reported as a complete run with zero rows.
            logger.LogError(ex, "Client import job {ImportJobId} failed", importJobId);
            importJob.Fail(ex.Message, clock);
            await db.SaveChangesAsync();
        }
    }

    private enum RowOutcome { Created, Skipped, Failed }

    private readonly record struct RowResult(RowOutcome Kind, string? Reason);

    private static async Task<RowResult> CreateIndividualAsync(
        ISender sender, ImportClientRow row, Guid agencyId)
    {
        if (!ClientImportValidator.TryParseDate(row.DateOfBirth ?? string.Empty, out var dateOfBirth))
            return new(RowOutcome.Failed, $"DateOfBirth '{row.DateOfBirth}' is not a date.");

        var result = await sender.Send(new CreateIndividualClientCommand(
            AgencyId: agencyId,
            AdvisorUserId: null,
            FirstName: row.FirstName ?? string.Empty,
            LastName: row.LastName ?? string.Empty,
            MaidenName: null,
            Gender: Enum.TryParse<Gender>(row.Gender, ignoreCase: true, out var gender)
                ? gender
                : Gender.Other,
            DateOfBirth: dateOfBirth,
            BirthPlace: null,
            Nationality: row.Nationality ?? string.Empty,
            MaritalStatus: null,
            FatherName: null,
            MotherName: null,
            Profession: row.Profession,
            Employer: null,
            DeclaredIncome: null,
            DeclaredIncomeCurrency: null,
            PreferredLanguage: row.PreferredLanguage,
            IdentityDocumentType: Enum.TryParse<IdentityDocumentType>(
                row.IdentityDocumentType, ignoreCase: true, out var docType)
                    ? docType
                    : IdentityDocumentType.NationalIdCard,
            IdentityDocumentNumber: row.IdentityDocumentNumber ?? string.Empty,
            IdentityDocumentIssuedOn: null,
            IdentityDocumentExpiresOn: null,
            PhoneNumbers: row.PhoneNumber is null ? [] : [row.PhoneNumber],
            Email: row.Email,
            Address: AddressOf(row),
            // Never auto-confirm in bulk: see the switch above.
            ConfirmNoDuplicate: false));

        if (result.IsFailure)
            return new(RowOutcome.Failed, result.Error);

        return result.Value.Outcome switch
        {
            CreateClientOutcome.Created => new(RowOutcome.Created, null),
            CreateClientOutcome.WarningPossibleDuplicatePhone =>
                new(RowOutcome.Skipped, "Phone already belongs to an active client — confirm by hand."),
            _ => new(RowOutcome.Failed, result.Value.Code ?? "Rejected."),
        };
    }

    private static async Task<RowResult> CreateLegalAsync(
        ISender sender, ImportClientRow row, Guid agencyId)
    {
        if (!ClientImportValidator.TryParseDate(row.IncorporationDate ?? string.Empty, out var incorporated))
            return new(RowOutcome.Failed, $"IncorporationDate '{row.IncorporationDate}' is not a date.");

        var result = await sender.Send(new CreateLegalClientCommand(
            AgencyId: agencyId,
            AdvisorUserId: null,
            LegalName: row.LegalName!,
            LegalFormCode: row.LegalFormCode ?? string.Empty,
            RegistrationNumber: row.RegistrationNumber ?? string.Empty,
            TaxIdNumber: row.TaxIdNumber,
            IncorporationDate: incorporated,
            HeadOfficeAddress: AddressOf(row) ?? new PostalAddressInput(null, null, null, null, null),
            PhoneNumbers: row.PhoneNumber is null ? [] : [row.PhoneNumber],
            Email: row.Email,
            PreferredLanguage: row.PreferredLanguage));

        if (result.IsFailure)
            return new(RowOutcome.Failed, result.Error);

        return result.Value.Outcome == CreateLegalClientOutcome.Created
            ? new(RowOutcome.Created, null)
            : new(RowOutcome.Failed, result.Value.Code ?? "Rejected.");
    }

    private static PostalAddressInput? AddressOf(ImportClientRow row)
        => row.AddressStreet is null && row.AddressCity is null && row.AddressCountry is null
            ? null
            : new PostalAddressInput(row.AddressStreet, row.AddressCity, null, row.AddressCountry, null);

    /// <summary>
    /// Agency of the row, then the import's default. Resolved by CODE because a spreadsheet
    /// filled in by a branch manager has "ABJ-PLT" in it, not a Guid.
    /// </summary>
    private static Guid? ResolveAgency(
        ImportClientRow row, IReadOnlyDictionary<string, Guid> byCode, Guid? fallback)
    {
        if (!string.IsNullOrWhiteSpace(row.AgencyCode)
            && byCode.TryGetValue(row.AgencyCode.Trim(), out var agencyId))
        {
            return agencyId;
        }

        return fallback;
    }

    /// <summary>
    /// What identifies the row in the failure report. Deliberately the name and never the
    /// identity document or the phone: this report is read, exported and pasted into tickets.
    /// </summary>
    private static string ReferenceOf(ImportClientRow row)
        => row.IsLegalEntity
            ? row.LegalName!
            : string.Join(' ', new[] { row.FirstName, row.LastName }
                .Where(p => !string.IsNullOrWhiteSpace(p)));

    private static IClientImportSourceReader ResolveReader(
        ClientImportSourceType sourceType, IServiceProvider sp) => sourceType switch
        {
            ClientImportSourceType.File => sp.GetRequiredService<ClientFileImportReader>(),
            ClientImportSourceType.GoogleSheet => sp.GetRequiredService<ClientGoogleSheetsImportReader>(),
            _ => throw new ArgumentOutOfRangeException(nameof(sourceType)),
        };
}

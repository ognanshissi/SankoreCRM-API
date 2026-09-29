namespace Sankore.Modules.Customers.Features.Import.ValidateImport;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// Checks a batch of rows against the same rules the create handlers enforce, WITHOUT creating
/// anything. Its point is that an operator importing four hundred clients finds out before the
/// run, not from a failure report afterwards.
///
/// Duplicate detection works exactly as it does at creation: equality on the blind index, never
/// a decryption.
/// </summary>
public sealed class ClientImportValidator(
    CustomersDbContext db,
    IBlindIndexer indexer,
    ICustomerSettings settings)
{
    public async Task<ValidateClientImportResponse> ValidateAsync(
        List<ImportClientRow> rows, Guid tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var minimumAge = await settings.GetIntAsync(tenantId, CustomerSettingKeys.MinimumAge, ct);

        var activeAgencyCodes = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId)
            .Select(c => c.AgencyCode)
            .Distinct()
            .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, ct);

        var legalForms = await db.LegalForms
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && f.IsActive)
            .Select(f => f.Code)
            .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, ct);

        // Blind indexes already taken in this tenant, loaded once rather than per row.
        var existingDocuments = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.IdentityDocumentNumberBlindIndex != null)
            .Select(c => c.IdentityDocumentNumberBlindIndex!)
            .ToHashSetAsync(StringComparer.Ordinal, ct);

        var existingRegistrations = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.RegistrationNumberBlindIndex != null)
            .Select(c => c.RegistrationNumberBlindIndex!)
            .ToHashSetAsync(StringComparer.Ordinal, ct);

        // Rows can also collide with each other, which the database cannot tell us.
        var seenDocuments = new HashSet<string>(StringComparer.Ordinal);
        var seenRegistrations = new HashSet<string>(StringComparer.Ordinal);

        var now = DateTimeOffset.UtcNow;
        var results = new List<ClientRowValidationResult>(rows.Count);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var errors = new List<string>();

            if (row.IsLegalEntity)
                ValidateLegalEntity(row, legalForms, existingRegistrations, seenRegistrations, errors);
            else
                ValidateIndividual(row, minimumAge, now, existingDocuments, seenDocuments, errors);

            if (string.IsNullOrWhiteSpace(row.PhoneNumber))
                errors.Add("PhoneNumber is required.");

            if (!string.IsNullOrWhiteSpace(row.AgencyCode)
                && activeAgencyCodes.Count > 0
                && !activeAgencyCodes.Contains(row.AgencyCode.Trim()))
            {
                errors.Add($"AgencyCode '{row.AgencyCode}' is not a known agency.");
            }

            results.Add(new ClientRowValidationResult(
                RowNumber: i + 1,
                DisplayName: row.IsLegalEntity
                    ? row.LegalName!
                    : string.Join(' ', new[] { row.FirstName, row.LastName }
                        .Where(p => !string.IsNullOrWhiteSpace(p))),
                ClientType: row.IsLegalEntity ? nameof(ClientType.Legal) : nameof(ClientType.Individual),
                AgencyCode: row.AgencyCode,
                IsValid: errors.Count == 0,
                Errors: errors));
        }

        var valid = results.Count(r => r.IsValid);

        return new ValidateClientImportResponse(
            TotalRows: rows.Count,
            ValidRows: valid,
            InvalidRows: rows.Count - valid,
            Rows: results);
    }

    private void ValidateIndividual(
        ImportClientRow row,
        int minimumAge,
        DateTimeOffset now,
        HashSet<string> existingDocuments,
        HashSet<string> seenDocuments,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(row.FirstName)) errors.Add("FirstName is required.");
        if (string.IsNullOrWhiteSpace(row.LastName)) errors.Add("LastName is required.");

        if (string.IsNullOrWhiteSpace(row.DateOfBirth))
        {
            errors.Add("DateOfBirth is required.");
        }
        else if (!TryParseDate(row.DateOfBirth, out var dob))
        {
            errors.Add($"DateOfBirth '{row.DateOfBirth}' is not a date (expected yyyy-MM-dd or dd/MM/yyyy).");
        }
        else if (AgeAt(dob, now) < minimumAge)
        {
            errors.Add($"Client is under the minimum age of {minimumAge}.");
        }

        if (string.IsNullOrWhiteSpace(row.IdentityDocumentNumber))
        {
            errors.Add("IdentityDocumentNumber is required.");
        }
        else
        {
            var index = indexer.Compute(BlindIndexPurpose.IdentityDocument, row.IdentityDocumentNumber);

            if (existingDocuments.Contains(index))
                errors.Add("An existing client already has this identity document.");

            if (!seenDocuments.Add(index))
                errors.Add("Duplicate identity document within the file.");
        }

        if (!string.IsNullOrWhiteSpace(row.Gender)
            && !Enum.TryParse<Gender>(row.Gender, ignoreCase: true, out _))
        {
            errors.Add($"Gender '{row.Gender}' is not one of Male, Female, Other.");
        }

        // Required rather than defaulted: guessing someone's nationality is the kind of wrong
        // that survives in a KYC file for years.
        if (string.IsNullOrWhiteSpace(row.Nationality))
            errors.Add("Nationality is required.");
    }

    private void ValidateLegalEntity(
        ImportClientRow row,
        HashSet<string> legalForms,
        HashSet<string> existingRegistrations,
        HashSet<string> seenRegistrations,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(row.LegalFormCode))
        {
            errors.Add("LegalFormCode is required for a legal entity.");
        }
        else if (!legalForms.Contains(row.LegalFormCode.Trim()))
        {
            errors.Add($"LegalFormCode '{row.LegalFormCode}' is not an active legal form.");
        }

        if (string.IsNullOrWhiteSpace(row.IncorporationDate))
            errors.Add("IncorporationDate is required for a legal entity.");
        else if (!TryParseDate(row.IncorporationDate, out _))
            errors.Add($"IncorporationDate '{row.IncorporationDate}' is not a date.");

        if (string.IsNullOrWhiteSpace(row.RegistrationNumber))
        {
            errors.Add("RegistrationNumber is required for a legal entity.");
        }
        else
        {
            var index = indexer.Compute(BlindIndexPurpose.RegistrationNumber, row.RegistrationNumber);

            if (existingRegistrations.Contains(index))
                errors.Add("An existing client already has this registration number.");

            if (!seenRegistrations.Add(index))
                errors.Add("Duplicate registration number within the file.");
        }
    }

    /// <summary>
    /// Day-first formats are tried before the invariant parser: an operator in Abidjan writing
    /// 02/04/1987 means 2 April, and the invariant parser would read 4 February.
    /// </summary>
    internal static bool TryParseDate(string value, out DateOnly date)
    {
        string[] formats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "dd.MM.yyyy"];

        return DateOnly.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out date)
               || DateOnly.TryParse(value.Trim(), CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out date);
    }

    private static int AgeAt(DateOnly birthDate, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var age = today.Year - birthDate.Year;
        if (birthDate > today.AddYears(-age)) age--;
        return age;
    }
}

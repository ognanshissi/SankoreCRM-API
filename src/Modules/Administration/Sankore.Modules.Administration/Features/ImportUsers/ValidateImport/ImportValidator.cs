namespace Sankore.Modules.Administration.Features.ImportUsers.ValidateImport;

using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;

/// <summary>
/// Validates a list of <see cref="ImportUserRow"/> against business rules
/// without creating any user. Reusable across all import sources.
/// </summary>
public sealed partial class ImportValidator(AdministrationDbContext db)
{
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    public async Task<ValidateImportResponse> ValidateAsync(
        List<ImportUserRow> rows, Guid tenantId, CancellationToken ct)
    {
        // Pre-load lookup data once
        var existingEmails = await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId)
            .Select(u => u.NormalizedEmail!)
            .ToHashSetAsync(ct);

        var activeAgencies = await db.Agencies
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && a.IsActive)
            .Select(a => a.Code)
            .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, ct);

        var defaultAgencyId = activeAgencies.FirstOrDefault();

        var validRoles = await db.Roles
            .Select(r => r.Name!)
            .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, ct);

        // Track intra-file duplicates
        var seenEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var results = new List<RowValidationResult>(rows.Count);

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var errors = new List<string>();

            // ── FirstName ───────────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(row.FirstName))
                errors.Add("FirstName is required.");

            // ── LastName ────────────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(row.LastName))
                errors.Add("LastName is required.");

            // ── Email ───────────────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(row.Email))
            {
                errors.Add("Email is required.");
            }
            else
            {
                if (!EmailRegex().IsMatch(row.Email))
                    errors.Add("Email format is invalid.");

                var normalized = row.Email.Trim().ToUpperInvariant();

                if (existingEmails.Contains(normalized))
                    errors.Add("Email already exists in tenant.");

                if (!seenEmails.Add(normalized))
                    errors.Add("Duplicate email within the file.");
            }

            // ── AgencyCode ──────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(row.AgencyCode)
                && !activeAgencies.Contains(row.AgencyCode.Trim()))
            {
                errors.Add($"AgencyCode '{row.AgencyCode}' not found in active agencies.");
            }

            // ── RoleCode ────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(row.RoleCode)
                && !validRoles.Contains(row.RoleCode.Trim()))
            {
                errors.Add($"RoleCode '{row.RoleCode}' not found.");
            }

            // ── DefaultLanguage ─────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(row.DefaultLanguage))
                errors.Add("DefaultLanguage is required.");

            results.Add(new RowValidationResult(
                RowNumber:  i + 1,
                FirstName:  row.FirstName,
                LastName:   row.LastName,
                Email:      row.Email,
                AgencyCode: row.AgencyCode,
                RoleCode:   row.RoleCode,
                IsValid:    errors.Count == 0,
                Errors:     errors));
        }

        var validCount = results.Count(r => r.IsValid);

        return new ValidateImportResponse(
            TotalRows:   rows.Count,
            ValidRows:   validCount,
            InvalidRows: rows.Count - validCount,
            Rows:        results);
    }
}

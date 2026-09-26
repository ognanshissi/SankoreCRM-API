namespace Sankore.Modules.Leads.Features.Import.ValidateImport;

using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Validates rows from any import source without capturing anything.
/// Deliberately faithful to what the import would actually do: the same parsing
/// (<see cref="LeadRowParser"/>), the same phone rule as <c>CaptureLeadValidator</c>,
/// and the same blind-index phone dedup as <c>CaptureLeadHandler</c> — so a row
/// reported valid here is one the import will accept.
/// </summary>
public sealed partial class LeadImportValidator(LeadsDbContext db, IPhoneBlindIndexer phoneBlindIndexer)
{
    [GeneratedRegex(@"^\+?[0-9\s\-]{8,20}$")]
    private static partial Regex PhoneRegex();

    public async Task<ValidateLeadImportResponse> ValidateAsync(
        List<ImportLeadRow> rows,
        ImportDefaults defaults,
        LeadSource fallbackSource,
        CancellationToken ct)
    {
        // ── Pre-compute the blind index of every usable phone in the file ──
        var indexByRow = new Dictionary<int, string>();
        for (var i = 0; i < rows.Count; i++)
        {
            var phone = rows[i].PhoneNumber?.Trim();
            if (!string.IsNullOrWhiteSpace(phone))
                indexByRow[i] = phoneBlindIndexer.Compute(phone);
        }

        // ── One query for the whole file, not one per row ──────────────────
        var fileIndexes = indexByRow.Values.Distinct().ToList();

        var existingByIndex = fileIndexes.Count == 0
            ? []
            : await db.Leads
                .Where(l => l.PhoneBlindIndex != null
                         && fileIndexes.Contains(l.PhoneBlindIndex)
                         && l.Status != LeadStatus.Lost
                         && l.Status != LeadStatus.Archived
                         && l.Status != LeadStatus.Disqualified)
                .Select(l => new { l.Id, l.PhoneBlindIndex })
                .ToDictionaryAsync(l => l.PhoneBlindIndex!, l => l.Id, ct);

        var seenInFile = new Dictionary<string, int>();
        var results = new List<LeadRowValidationResult>(rows.Count);

        for (var i = 0; i < rows.Count; i++)
        {
            var raw = rows[i];
            var parsed = LeadRowParser.Parse(raw, defaults, fallbackSource);
            var errors = new List<string>(parsed.Errors);

            var phone = raw.PhoneNumber?.Trim();

            // ── Phone shape: the rule CaptureLeadValidator will apply ──────
            if (!string.IsNullOrWhiteSpace(phone) && !PhoneRegex().IsMatch(phone))
                errors.Add($"PhoneNumber '{phone}' does not match the expected format.");

            // ── Duplicates: inside the file, then against existing leads ──
            var isDuplicate = false;
            Guid? duplicateOf = null;

            if (indexByRow.TryGetValue(i, out var blindIndex))
            {
                if (seenInFile.TryGetValue(blindIndex, out var firstRow))
                {
                    isDuplicate = true;
                    errors.Add($"Duplicate phone number within the file (first seen on row {firstRow}).");
                }
                else
                {
                    seenInFile[blindIndex] = i + 1;
                }

                if (existingByIndex.TryGetValue(blindIndex, out var leadId))
                {
                    isDuplicate = true;
                    duplicateOf = leadId;
                    errors.Add("A lead with this phone number already exists — the row would be skipped.");
                }
            }

            results.Add(new LeadRowValidationResult(
                RowNumber:         i + 1,
                FullName:          parsed.Row?.FullName ?? raw.FullName,
                PhoneNumber:       phone,
                InterestedProduct: parsed.Row?.InterestedProduct ?? raw.InterestedProduct,
                IsValid:           errors.Count == 0,
                IsDuplicate:       isDuplicate,
                DuplicateOfLeadId: duplicateOf,
                Errors:            errors));
        }

        var valid = results.Count(r => r.IsValid);

        return new ValidateLeadImportResponse(
            TotalRows:     rows.Count,
            ValidRows:     valid,
            InvalidRows:   rows.Count - valid,
            DuplicateRows: results.Count(r => r.IsDuplicate),
            Rows:          results);
    }
}

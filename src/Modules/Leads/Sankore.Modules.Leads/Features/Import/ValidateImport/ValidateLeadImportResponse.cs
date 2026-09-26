namespace Sankore.Modules.Leads.Features.Import.ValidateImport;

public sealed record ValidateLeadImportResponse(
    int TotalRows,
    int ValidRows,
    int InvalidRows,
    /// <summary>Rows that would be captured but immediately skipped as duplicates.</summary>
    int DuplicateRows,
    IReadOnlyList<LeadRowValidationResult> Rows);

public sealed record LeadRowValidationResult(
    int RowNumber,
    string? FullName,
    string? PhoneNumber,
    string? InterestedProduct,
    bool IsValid,
    bool IsDuplicate,
    /// <summary>Existing lead this row would collide with, when known.</summary>
    Guid? DuplicateOfLeadId,
    IReadOnlyList<string> Errors);

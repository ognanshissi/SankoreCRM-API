namespace Sankore.Modules.Administration.Features.ImportUsers.ValidateImport;

public sealed record ValidateImportResponse(
    int TotalRows,
    int ValidRows,
    int InvalidRows,
    IReadOnlyList<RowValidationResult> Rows);

public sealed record RowValidationResult(
    int RowNumber,
    string FirstName,
    string LastName,
    string Email,
    string? AgencyCode,
    string? RoleCode,
    bool IsValid,
    IReadOnlyList<string> Errors);

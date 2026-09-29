namespace Sankore.Modules.Customers.Features.Import.ValidateImport;

public sealed record ValidateClientImportResponse(
    int TotalRows,
    int ValidRows,
    int InvalidRows,
    IReadOnlyList<ClientRowValidationResult> Rows);

/// <summary>
/// One row's verdict. Carries the display name and the agency, never the identity document or
/// the phone number — a validation report is routinely mailed around or pasted into a ticket.
/// </summary>
public sealed record ClientRowValidationResult(
    int RowNumber,
    string DisplayName,
    string ClientType,
    string? AgencyCode,
    bool IsValid,
    IReadOnlyList<string> Errors);

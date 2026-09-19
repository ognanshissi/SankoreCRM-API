namespace Sankore.Modules.Leads.Features.Import;

using Sankore.Modules.Leads.Domain;

public sealed record LeadImportStatusResponse(
    Guid ImportJobId,
    LeadImportStatus Status,
    string OriginalFileName,
    int TotalRows,
    int Succeeded,
    int Skipped,
    int Failed,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage,
    IReadOnlyList<ImportRowFailure>? Failures);

public sealed record ImportRowFailure(int Row, string PhoneNumber, string Error);

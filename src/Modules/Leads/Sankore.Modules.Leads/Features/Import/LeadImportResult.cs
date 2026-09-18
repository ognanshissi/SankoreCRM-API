namespace Sankore.Modules.Leads.Features.Import;

public sealed record LeadImportResult(
    int Succeeded,
    int Skipped,
    int Failed,
    IReadOnlyList<ImportRowFailure> Failures);

public sealed record ImportRowFailure(int Row, string PhoneNumber, string Error);

namespace Sankore.Modules.Leads.Features.Bulk;

/// <summary>
/// Summary returned by every bulk operation.
/// Partial success is allowed: failed items are reported individually
/// while succeeded items are already committed.
/// </summary>
public sealed record BulkOperationResult(
    int Succeeded,
    int Failed,
    IReadOnlyList<BulkFailure> Failures);

public sealed record BulkFailure(Guid LeadId, string Error);

namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// One line of the unified client timeline, fed by consumers of other modules' integration events.
/// <see cref="DedupKey"/> is unique per tenant: a redelivered message therefore updates nothing
/// and duplicates nothing.
/// <see cref="ReferenceType"/>/<see cref="ReferenceId"/> is an opaque reference — never a
/// physical foreign key into another module's schema.
/// </summary>
public sealed class ClientTimelineEntry
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ClientId { get; private set; }
    public string SourceModule { get; private set; } = default!;
    public string EntryType { get; private set; } = default!;
    public DateTimeOffset OccurredAt { get; private set; }
    public string Summary { get; private set; } = default!;
    public string? ReferenceType { get; private set; }
    public string? ReferenceId { get; private set; }
    public string DedupKey { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }

    private ClientTimelineEntry() { } // EF Core

    public static ClientTimelineEntry Create(
        Guid tenantId,
        Guid clientId,
        string sourceModule,
        string entryType,
        DateTimeOffset occurredAt,
        string summary,
        string? referenceType,
        string? referenceId,
        string dedupKey)
    {
        if (string.IsNullOrWhiteSpace(sourceModule))
            throw new DomainException("Source module is required.", "ClientTimelineEntry.SourceModule.Required");
        if (string.IsNullOrWhiteSpace(entryType))
            throw new DomainException("Entry type is required.", "ClientTimelineEntry.EntryType.Required");
        if (string.IsNullOrWhiteSpace(summary))
            throw new DomainException("Summary is required.", "ClientTimelineEntry.Summary.Required");
        if (string.IsNullOrWhiteSpace(dedupKey))
            throw new DomainException("Dedup key is required.", "ClientTimelineEntry.DedupKey.Required");

        return new ClientTimelineEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            SourceModule = sourceModule.Trim(),
            EntryType = entryType.Trim(),
            OccurredAt = occurredAt,
            Summary = summary.Trim(),
            ReferenceType = string.IsNullOrWhiteSpace(referenceType) ? null : referenceType.Trim(),
            ReferenceId = string.IsNullOrWhiteSpace(referenceId) ? null : referenceId.Trim(),
            DedupKey = dedupKey.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Re-parents the entry onto the surviving client of a merge.</summary>
    internal void ReassignTo(Guid clientId) => ClientId = clientId;
}

namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Historized segment membership: the nightly segmentation job closes the current row and opens
/// a new one instead of overwriting, so "since when is this client Premium" stays answerable.
/// </summary>
public sealed class ClientSegmentHistory
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ClientId { get; private set; }
    public string SegmentCode { get; private set; } = default!;
    public DateTimeOffset ValidFrom { get; private set; }
    public DateTimeOffset? ValidTo { get; private set; }

    /// <summary>Code of the segmentation rule that matched, for traceability.</summary>
    public string? RuleCode { get; private set; }

    public bool IsActive => ValidTo is null;

    private ClientSegmentHistory() { } // EF Core

    public static ClientSegmentHistory Open(
        Guid tenantId,
        Guid clientId,
        string segmentCode,
        DateTimeOffset validFrom,
        string? ruleCode)
    {
        if (string.IsNullOrWhiteSpace(segmentCode))
            throw new DomainException("Segment code is required.", "ClientSegmentHistory.SegmentCode.Required");

        return new ClientSegmentHistory
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            SegmentCode = segmentCode.Trim(),
            ValidFrom = validFrom,
            RuleCode = string.IsNullOrWhiteSpace(ruleCode) ? null : ruleCode.Trim(),
        };
    }

    public void Close(DateTimeOffset at) => ValidTo ??= at;
}

namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// A label attached to a lead for segmentation, filtering and bulk actions.
/// Tags are normalised to lowercase-trimmed strings to avoid duplicates.
/// </summary>
public sealed class LeadTag
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }

    /// <summary>Normalised tag value (lowercase, trimmed, max 50 chars).</summary>
    public string Tag { get; private set; } = string.Empty;

    public Guid AddedBy { get; private set; }
    public DateTimeOffset AddedAt { get; private set; }

    private LeadTag() { } // EF Core

    public static LeadTag Create(Guid tenantId, Guid leadId, string tag, Guid addedBy)
        => new()
        {
            Id       = Guid.NewGuid(),
            TenantId = tenantId,
            LeadId   = leadId,
            Tag      = tag.Trim().ToLowerInvariant(),
            AddedBy  = addedBy,
            AddedAt  = DateTimeOffset.UtcNow,
        };
}

namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// One code translation between the CRM and one external system (INT-04).
///
/// <para>
/// Per connection and not per tenant: an IMF connected to a CBS and to two insurers has three
/// different vocabularies for the same CRM product, and a tenant-wide table would make the last
/// one written win.
/// </para>
///
/// <para>
/// A missing row is a <c>Technical</c> failure naming the domain and the code — never a
/// pass-through of the CRM code. Sending an unmapped code is how a customer is created at the
/// CBS with a profession nobody can read, and the row looks successful.
/// </para>
/// </summary>
public sealed class IntegrationMapping : AggregateRoot
{
    public Guid Id { get; private set; }

    public Guid ConnectionId { get; private set; }

    public MappingDomain Domain { get; private set; }

    public string CrmCode { get; private set; } = string.Empty;

    public string ExternalCode { get; private set; } = string.Empty;

    /// <summary>Optional operator-facing label, to make the administration screen readable.</summary>
    public string? Label { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public uint Version { get; private set; }

    private IntegrationMapping() { }

    public static IntegrationMapping Create(
        Guid tenantId,
        Guid connectionId,
        MappingDomain domain,
        string crmCode,
        string externalCode,
        Guid createdBy,
        TimeProvider clock,
        string? label = null,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (connectionId == Guid.Empty) throw new DomainException("ConnectionId is required.");
        if (string.IsNullOrWhiteSpace(crmCode)) throw new DomainException("A CRM code is required.");
        if (string.IsNullOrWhiteSpace(externalCode))
            throw new DomainException("An external code is required.");

        var now = clock.GetUtcNow();

        return new IntegrationMapping
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            Domain = domain,
            // Trimmed but NOT case-folded: an external system's codes are its own, and "CI" is
            // not necessarily "ci" on the other side.
            CrmCode = crmCode.Trim(),
            ExternalCode = externalCode.Trim(),
            Label = label?.Trim(),
            CreatedAt = now,
            CreatedBy = createdBy,
            UpdatedAt = now,
        };
    }

    public void Update(string externalCode, string? label, Guid updatedBy, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(externalCode))
            throw new DomainException("An external code is required.");

        ExternalCode = externalCode.Trim();
        Label = label?.Trim();
        UpdatedBy = updatedBy;
        UpdatedAt = clock.GetUtcNow();
    }
}

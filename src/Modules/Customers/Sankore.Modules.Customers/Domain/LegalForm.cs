namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// A legal form a corporate client can take (SARL, SA, GIE, …). The list is seeded per tenant
/// from <see cref="DefaultLegalForms"/> and then curated by the tenant.
/// Retiring one only deactivates it, so clients already using it keep a resolvable code.
/// </summary>
public sealed class LegalForm
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Code { get; private set; } = default!;
    public string Label { get; private set; } = default!;
    public bool IsActive { get; private set; }
    public int DisplayOrder { get; private set; }

    private LegalForm() { } // EF Core

    public static LegalForm Create(Guid tenantId, string code, string label, int displayOrder)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new DomainException("Legal form code is required.", "LegalForm.Code.Required");
        if (string.IsNullOrWhiteSpace(label))
            throw new DomainException("Legal form label is required.", "LegalForm.Label.Required");

        return new LegalForm
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Code = code.Trim(),
            Label = label.Trim(),
            IsActive = true,
            DisplayOrder = displayOrder,
        };
    }

    public void Deactivate() => IsActive = false;
}

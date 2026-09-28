namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// A beneficial owner of a legal client: either an existing client of the tenant
/// (<see cref="ForClient"/>) or an external person captured inline (<see cref="ForExternalPerson"/>).
/// Historized — a change closes the previous row instead of editing it, so the AML file
/// can always be rebuilt as it stood on any past date.
/// </summary>
public sealed class BeneficialOwner
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid LegalClientId { get; private set; }
    public Guid? LinkedClientId { get; private set; }

    public string? ExternalFullName { get; private set; }
    public string? ExternalNationality { get; private set; }
    public DateOnly? ExternalDateOfBirth { get; private set; }
    public string? EncryptedExternalDocumentNumber { get; private set; }
    public string? ExternalDocumentBlindIndex { get; private set; }

    public decimal OwnershipPercentage { get; private set; }
    public ControlType ControlType { get; private set; }
    public DateTimeOffset ValidFrom { get; private set; }
    public DateTimeOffset? ValidTo { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsActive => ValidTo is null;

    private BeneficialOwner() { } // EF Core

    public static BeneficialOwner ForClient(
        Guid tenantId,
        Guid legalClientId,
        Guid linkedClientId,
        decimal ownershipPercentage,
        ControlType controlType,
        DateTimeOffset validFrom,
        Guid createdBy)
    {
        if (linkedClientId == Guid.Empty)
            throw new DomainException("Linked client is required.", "BeneficialOwner.LinkedClient.Required");
        if (linkedClientId == legalClientId)
            throw new DomainException("A legal entity cannot own itself.", "BeneficialOwner.Self.Forbidden");
        GuardOwnership(ownershipPercentage);

        return new BeneficialOwner
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            LegalClientId = legalClientId,
            LinkedClientId = linkedClientId,
            OwnershipPercentage = ownershipPercentage,
            ControlType = controlType,
            ValidFrom = validFrom,
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    public static BeneficialOwner ForExternalPerson(
        Guid tenantId,
        Guid legalClientId,
        string externalFullName,
        string? externalNationality,
        DateOnly? externalDateOfBirth,
        string? encryptedExternalDocumentNumber,
        string? externalDocumentBlindIndex,
        decimal ownershipPercentage,
        ControlType controlType,
        DateTimeOffset validFrom,
        Guid createdBy)
    {
        if (string.IsNullOrWhiteSpace(externalFullName))
            throw new DomainException("Beneficial owner name is required.", "BeneficialOwner.FullName.Required");
        GuardOwnership(ownershipPercentage);

        return new BeneficialOwner
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            LegalClientId = legalClientId,
            ExternalFullName = externalFullName.Trim(),
            ExternalNationality = string.IsNullOrWhiteSpace(externalNationality) ? null : externalNationality.Trim(),
            ExternalDateOfBirth = externalDateOfBirth,
            EncryptedExternalDocumentNumber = encryptedExternalDocumentNumber,
            ExternalDocumentBlindIndex = externalDocumentBlindIndex,
            OwnershipPercentage = ownershipPercentage,
            ControlType = controlType,
            ValidFrom = validFrom,
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    public void Close(DateTimeOffset at) => ValidTo ??= at;

    private static void GuardOwnership(decimal ownershipPercentage)
    {
        if (ownershipPercentage is < 0m or > 100m)
            throw new DomainException("Ownership percentage must be between 0 and 100.", "BeneficialOwner.Ownership.OutOfRange");
    }
}

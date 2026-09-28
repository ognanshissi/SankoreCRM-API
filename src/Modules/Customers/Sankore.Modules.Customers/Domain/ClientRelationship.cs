namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// A link between a client and either another client of the tenant (<see cref="ToClient"/>)
/// or a person who is not a client (<see cref="ToExternal"/>).
/// Client-to-client links are created in pairs: each row points at its mirror through
/// <see cref="ReciprocalRelationshipId"/>, so closing one closes the story on both sides.
/// </summary>
public sealed class ClientRelationship
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ClientId { get; private set; }
    public RelationshipType Type { get; private set; }

    public Guid? RelatedClientId { get; private set; }
    public string? ExternalFullName { get; private set; }
    public string? ExternalPhoneEncrypted { get; private set; }
    public string? ExternalPhoneBlindIndex { get; private set; }
    public DateOnly? ExternalDateOfBirth { get; private set; }
    public string? EncryptedExternalDocumentNumber { get; private set; }

    public Guid? ReciprocalRelationshipId { get; private set; }
    public DateTimeOffset ValidFrom { get; private set; }
    public DateTimeOffset? ValidTo { get; private set; }
    public string? CloseReason { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsActive => ValidTo is null;

    private ClientRelationship() { } // EF Core

    public static ClientRelationship ToClient(
        Guid tenantId,
        Guid clientId,
        RelationshipType type,
        Guid relatedClientId,
        DateTimeOffset validFrom,
        Guid createdBy)
    {
        if (relatedClientId == Guid.Empty)
            throw new DomainException("Related client is required.", "ClientRelationship.RelatedClient.Required");
        if (relatedClientId == clientId)
            throw new DomainException("A client cannot be related to itself.", "ClientRelationship.Self.Forbidden");

        return new ClientRelationship
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Type = type,
            RelatedClientId = relatedClientId,
            ValidFrom = validFrom,
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    public static ClientRelationship ToExternal(
        Guid tenantId,
        Guid clientId,
        RelationshipType type,
        string externalFullName,
        string? externalPhoneEncrypted,
        string? externalPhoneBlindIndex,
        DateOnly? externalDateOfBirth,
        string? encryptedExternalDocumentNumber,
        DateTimeOffset validFrom,
        Guid createdBy)
    {
        if (string.IsNullOrWhiteSpace(externalFullName))
            throw new DomainException("Related person name is required.", "ClientRelationship.ExternalFullName.Required");

        return new ClientRelationship
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Type = type,
            ExternalFullName = externalFullName.Trim(),
            ExternalPhoneEncrypted = externalPhoneEncrypted,
            ExternalPhoneBlindIndex = externalPhoneBlindIndex,
            ExternalDateOfBirth = externalDateOfBirth,
            EncryptedExternalDocumentNumber = encryptedExternalDocumentNumber,
            ValidFrom = validFrom,
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    internal void LinkReciprocal(Guid id) => ReciprocalRelationshipId = id;

    public void Close(string? reason, DateTimeOffset at)
    {
        if (ValidTo is not null)
            return;

        ValidTo = at;
        CloseReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }

    /// <summary>Re-parents the relationship onto the surviving client of a merge.</summary>
    internal void ReassignTo(Guid clientId) => ClientId = clientId;
}

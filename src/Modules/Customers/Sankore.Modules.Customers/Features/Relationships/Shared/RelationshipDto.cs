using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Crypto;

namespace Sankore.Modules.Customers.Features.Relationships.Shared;

/// <summary>
/// Read model of a <see cref="ClientRelationship"/>. Data about a person who is not
/// a client (phone, date of birth, document number) is returned masked: those values
/// belong to a third party who never consented to appear in a listing in clear.
/// </summary>
public sealed record RelationshipDto(
    Guid Id,
    RelationshipType Type,
    Guid? RelatedClientId,
    string? RelatedClientNumber,
    string? RelatedClientDisplayName,
    string? ExternalFullName,
    string? MaskedExternalPhone,
    string? MaskedExternalDateOfBirth,
    string? MaskedExternalDocumentNumber,
    Guid? ReciprocalRelationshipId,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    string? CloseReason,
    bool IsActive);

/// <summary>
/// The relationship types that make a person a dependent of the client. Kept in one
/// place because both the add and the close slice recompute
/// <c>Client.DependentsCount</c> from it.
/// </summary>
internal static class DependentRelationshipTypes
{
    public static bool Counts(RelationshipType type)
        => type is RelationshipType.Dependent or RelationshipType.Child;
}

internal static class RelationshipProtection
{
    /// <summary>Decrypts then masks the external party's data — never returns clear text.</summary>
    public static RelationshipDto ToDto(
        IFieldEncryptor encryptor,
        ClientRelationship relationship,
        string? relatedClientNumber,
        string? relatedClientDisplayName) => new(
        relationship.Id,
        relationship.Type,
        relationship.RelatedClientId,
        relatedClientNumber,
        relatedClientDisplayName,
        relationship.ExternalFullName,
        SensitiveValueMasker.MaskPhone(encryptor.Decrypt(relationship.ExternalPhoneEncrypted)),
        SensitiveValueMasker.MaskDate(relationship.ExternalDateOfBirth),
        SensitiveValueMasker.MaskDocument(encryptor.Decrypt(relationship.EncryptedExternalDocumentNumber)),
        relationship.ReciprocalRelationshipId,
        relationship.ValidFrom,
        relationship.ValidTo,
        relationship.CloseReason,
        relationship.ValidTo is null);
}

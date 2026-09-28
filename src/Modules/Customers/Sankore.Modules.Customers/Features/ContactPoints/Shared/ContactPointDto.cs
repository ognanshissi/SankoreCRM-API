using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Crypto;

namespace Sankore.Modules.Customers.Features.ContactPoints.Shared;

/// <summary>
/// Read model of a <see cref="ClientContactPoint"/>. The clear value NEVER leaves
/// the module through this DTO: <see cref="MaskedValue"/> is the partially hidden
/// rendering produced by <see cref="SensitiveValueMasker"/>. The clear text is
/// only ever served by the dedicated, audited and rate-limited reveal endpoint.
/// </summary>
public sealed record ContactPointDto(
    Guid Id,
    ContactPointType Type,
    string MaskedValue,
    string? Label,
    bool IsPrimary,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    bool IsActive);

/// <summary>
/// Maps a <see cref="ContactPointType"/> onto the crypto primitives that apply to
/// it: which blind-index domain the value belongs to, and how it is masked on read.
/// Keeping this in one place is what guarantees that a phone stored today and a
/// phone searched tomorrow land on the same blind index.
/// </summary>
internal static class ContactPointProtection
{
    /// <summary>
    /// Blind-index domain of the value. The indexer normalizes the value itself
    /// according to this purpose, so callers pass the raw user input.
    /// </summary>
    public static BlindIndexPurpose PurposeFor(ContactPointType type) => type switch
    {
        ContactPointType.Phone => BlindIndexPurpose.Phone,
        ContactPointType.Email => BlindIndexPurpose.Email,
        _ => BlindIndexPurpose.PostalAddress
    };

    /// <summary>Type-appropriate mask; never returns the clear value.</summary>
    public static string Mask(ContactPointType type, string? clear) => type switch
    {
        ContactPointType.Phone => SensitiveValueMasker.MaskPhone(clear) ?? string.Empty,
        ContactPointType.Email => SensitiveValueMasker.MaskEmail(clear) ?? string.Empty,
        _ => SensitiveValueMasker.MaskGeneric(clear) ?? string.Empty
    };

    /// <summary>Decrypts then masks in one step — the only way this zone reads a value.</summary>
    public static string MaskEncrypted(IFieldEncryptor encryptor, ContactPointType type, string? encryptedValue)
        => Mask(type, encryptor.Decrypt(encryptedValue));

    public static ContactPointDto ToDto(IFieldEncryptor encryptor, ClientContactPoint cp) => new(
        cp.Id,
        cp.Type,
        MaskEncrypted(encryptor, cp.Type, cp.EncryptedValue),
        cp.Label,
        cp.IsPrimary,
        cp.ValidFrom,
        cp.ValidTo,
        cp.ValidTo is null);
}

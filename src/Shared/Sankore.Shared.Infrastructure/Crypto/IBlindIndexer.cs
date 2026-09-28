namespace Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// Deterministic, keyed one-way index over a sensitive value so that an
/// encrypted column stays searchable by exact match (duplicate detection,
/// "find the client with this phone number") without ever storing the clear
/// value or a plain unsalted hash.
/// </summary>
public interface IBlindIndexer
{
    /// <summary>
    /// Lower-case hexadecimal HMAC-SHA256 of the value normalized according to
    /// <paramref name="purpose"/>. The purpose is part of the hashed input, so
    /// the same string indexed for two purposes yields two unrelated indexes.
    /// </summary>
    string Compute(BlindIndexPurpose purpose, string value);
}

/// <summary>
/// What a blind index is computed FOR. Drives both the normalization applied to
/// the value and the domain separation of the resulting hash.
/// </summary>
public enum BlindIndexPurpose
{
    Phone,
    Email,
    IdentityDocument,
    DateOfBirth,
    RegistrationNumber,
    PostalAddress
}

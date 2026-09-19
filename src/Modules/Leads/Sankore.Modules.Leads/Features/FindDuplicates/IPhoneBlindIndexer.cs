namespace Sankore.Modules.Leads.Features.FindDuplicates;

/// <summary>
/// Computes an HMAC-SHA256 blind index from a phone number so that
/// duplicate detection can rely on an indexed, non-reversible column
/// instead of querying the plaintext phone number.
/// </summary>
public interface IPhoneBlindIndexer
{
    /// <summary>
    /// Normalizes the phone number (last 8 significant digits) then returns
    /// the lowercase hex HMAC-SHA256 using the configured secret key.
    /// </summary>
    string Compute(string phoneNumber);
}

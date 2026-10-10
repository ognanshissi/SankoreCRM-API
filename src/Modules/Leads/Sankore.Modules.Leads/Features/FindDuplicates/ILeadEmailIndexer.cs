namespace Sankore.Modules.Leads.Features.FindDuplicates;

/// <summary>
/// Computes the blind index of an e-mail so duplicate detection can match on an indexed,
/// non-reversible column instead of the encrypted one (BNF-B-01).
///
/// <para>
/// The counterpart of <see cref="IPhoneBlindIndexer"/>, and a separate interface rather than a
/// second method on it: the two are computed from different keys' worth of purpose
/// (<c>Email</c> vs the phone indexer's unprefixed input) and a caller that only has an e-mail
/// should not be handed a phone API.
/// </para>
/// </summary>
public interface ILeadEmailIndexer
{
    /// <summary>
    /// Lower-case hex HMAC-SHA256 over <c>Email:&lt;trimmed, lower-cased address&gt;</c>, or
    /// <c>null</c> when there is no address.
    ///
    /// <para>
    /// Null-in / null-out matters: a lead with no e-mail must store a null index, not the index of
    /// an empty string — otherwise every lead with no e-mail shares one index value and they all
    /// match each other.
    /// </para>
    /// </summary>
    string? Compute(string? email);
}

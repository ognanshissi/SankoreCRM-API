namespace Sankore.Modules.Integration.Features.Telephony;

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// The one place a telephone number is turned into what the telephony tables hold (TEL-B-03).
///
/// <para>
/// <b>Why a service rather than two calls at each site.</b> A number must be normalised the same
/// way before it is indexed and before it is encrypted, or a number stored by the webhook and the
/// same number presented by the catch-up sweep produce two different indexes and the caller-id
/// lookup silently stops matching. Normalising twice in two places is how that happens, so it
/// happens once, here.
/// </para>
///
/// <para>
/// <b>This module's own key.</b> Blind indexes are per module by design — M01's and M13's are
/// computed under different keys and this module could not reproduce either. That is why the
/// attachment of TEL-B-09 ASKS those modules to look a number up instead of computing an index
/// it could compare itself: the index here is for finding a call by its own number, never for
/// finding a person.
/// </para>
/// </summary>
internal sealed class TelephonyNumberProtector(
    [FromKeyedServices(Infrastructure.Crypto.IntegrationFieldProtection.Key)] IFieldEncryptor encryptor,
    [FromKeyedServices(Infrastructure.Crypto.IntegrationFieldProtection.Key)] IBlindIndexer indexer)
{
    /// <summary>
    /// A number as the tables hold it: the ciphertext and the index.
    ///
    /// <para>
    /// Both are bytes rather than text. The encryptor answers the module's <c>v1:nonce:tag:ct</c>
    /// envelope as a string and the indexer lower-case hex; storing those as text would double
    /// the column and invite somebody to compare an index with <c>LIKE</c>, where the only
    /// comparison that is ever correct is equality on the whole digest.
    /// </para>
    /// </summary>
    internal (byte[] Encrypted, byte[] BlindIndex) Protect(string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return ([], []);

        var trimmed = number.Trim();

        // Encrypt is declared null-in / null-out; a non-blank input never produces null, and the
        // throw says so rather than letting a misconfigured key write an EMPTY ciphertext column
        // — which would read, later, as a number that was never captured.
        var envelope = encryptor.Encrypt(trimmed)
            ?? throw new InvalidOperationException(
                "The integration field encryptor returned nothing for a non-blank number.");

        return (
            Encoding.UTF8.GetBytes(envelope),
            Convert.FromHexString(indexer.Compute(BlindIndexPurpose.Phone, trimmed)));
    }

    /// <summary>
    /// The index alone, for looking a call up by one of its numbers without decrypting anything.
    /// </summary>
    internal byte[] IndexOf(string number)
        => Convert.FromHexString(indexer.Compute(BlindIndexPurpose.Phone, number.Trim()));

    /// <summary>
    /// The number back in clear. Used by exactly two callers — the click-to-call that must dial
    /// it and the attachment that must ask another module to resolve it — and by nothing that
    /// renders, logs or serialises.
    ///
    /// <para>
    /// Null rather than throwing on a payload that will not open: a call whose number cannot be
    /// read is still a call to journal, and a key problem must not take the journal down with it.
    /// </para>
    /// </summary>
    internal string? Reveal(byte[]? encrypted)
    {
        if (encrypted is not { Length: > 0 }) return null;

        try
        {
            return encryptor.Decrypt(Encoding.UTF8.GetString(encrypted));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return null;
        }
    }
}

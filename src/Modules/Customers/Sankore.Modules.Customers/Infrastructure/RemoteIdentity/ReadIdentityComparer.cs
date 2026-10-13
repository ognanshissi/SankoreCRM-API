namespace Sankore.Modules.Customers.Infrastructure.RemoteIdentity;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain.Matching;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// Compares what the OCR READ on a customer's identity document against what the client record
/// says (KRM-B-08, section 3.2 — « Nom, prénom et date de naissance lus sur la pièce cohérents
/// avec la fiche client, comparaison normalisée, sans accents ni casse »).
///
/// <para>
/// <b>Why in M01.</b> The surname and given names are clear columns here and the date of birth is
/// AES-256-GCM with an HMAC index beside it. M02 holds the reading; M01 holds the record. Handing
/// M02 the record would copy a regulated field into a module with no key for it, and handing M01's
/// comparison to M02 would put the normalisation rule in two places — <see cref="SearchKeyBuilder"/>
/// is already the single definition of "accent-free, upper-cased, punctuation collapsed" and the
/// specification asks for exactly that rule.
/// </para>
///
/// <para>
/// <b>No timing care here, unlike <see cref="RemoteIdentityVerifier"/> next door, and the
/// difference is who is asking.</b> That one answers an anonymous customer typing guesses at a
/// public endpoint. This one answers the routing of a file that has already been submitted, from a
/// background consumer, with values nobody outside the institution supplied. There is no oracle to
/// protect: the caller already holds both sides.
/// </para>
/// </summary>
// Public for the reason its neighbours are: the facade is internal, the test project builds its
// doubles against this type, and a public facade constructor cannot take an internal dependency.
public sealed class ReadIdentityComparer(
    CustomersDbContext db,
    IBlindIndexer indexer,
    ILogger<ReadIdentityComparer> logger)
{
    public async Task<bool> MatchesAsync(
        Guid tenantId,
        Guid clientId,
        string? readSurname,
        string? readGivenNames,
        DateOnly? readBirthDate,
        CancellationToken ct)
    {
        // Nothing read is NOT a match. A document whose surname the extractor could not produce is
        // not a document that agrees with the record — it is a document nobody checked. The
        // specification's condition is a positive one ("cohérents"), and the only honest answer to
        // "is this consistent with the record" when there is nothing to compare is no. The file
        // then routes to a human, which is the safe direction.
        if (string.IsNullOrWhiteSpace(readSurname)
            || string.IsNullOrWhiteSpace(readGivenNames)
            || readBirthDate is null)
        {
            logger.LogInformation(
                "Identity consistency check on client {ClientId}: the document reading is "
                + "incomplete, so it cannot be declared consistent.",
                clientId);
            return false;
        }

        // IgnoreQueryFilters with an explicit tenant predicate: the caller is a consumer, whose
        // ambient tenant is the one it established for itself but which this contract does not
        // trust — every method here takes the tenant explicitly for that reason.
        var client = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.Id == clientId)
            .Select(c => new { c.FirstName, c.LastName, c.DateOfBirthBlindIndex })
            .FirstOrDefaultAsync(ct);

        if (client is null || string.IsNullOrEmpty(client.DateOfBirthBlindIndex))
        {
            logger.LogInformation(
                "Identity consistency check refused for client {ClientId}: unknown, or no recorded "
                + "date of birth to compare against.",
                clientId);
            return false;
        }

        // The date on its blind index, never by decrypting — the same reasoning as
        // RemoteIdentityVerifier: an HMAC over the ISO form is exactly what equality on a date
        // needs, and the plaintext of a regulated field never has to exist in memory for it.
        var readIndex = indexer.Compute(
            BlindIndexPurpose.DateOfBirth,
            SensitiveValueNormalizer.NormalizeDateOfBirth(readBirthDate.Value));

        var birthMatches = string.Equals(
            readIndex, client.DateOfBirthBlindIndex, StringComparison.Ordinal);

        var surnameMatches = SurnameMatches(readSurname, client.LastName);
        var givenNamesMatch = GivenNamesMatch(readGivenNames, client.FirstName);

        if (!surnameMatches || !givenNamesMatch || !birthMatches)
        {
            // WHICH of the three disagreed, and never the values. An operator diagnosing why every
            // remote file of a tenant routes to a human needs to know it is the surname rule and
            // not the date; printing the two names would put a customer's identity in a log line,
            // which §8 of the specification forbids outright.
            logger.LogInformation(
                "Identity consistency check on client {ClientId}: surname {Surname}, "
                + "given names {Given}, date of birth {Birth}.",
                clientId, surnameMatches, givenNamesMatch, birthMatches);
        }

        return surnameMatches && givenNamesMatch && birthMatches;
    }

    /// <summary>
    /// Normalised equality on the surname: accents removed, case folded, punctuation and spacing
    /// collapsed — <see cref="SearchKeyBuilder.NormalizeTerm"/>, which is the platform's single
    /// definition of that rule and already backs client search.
    ///
    /// <para>
    /// Strict, unlike the given names below. A surname is one field on both sides and an MRZ
    /// carries it as one; a difference there is a different person or a misread document, and both
    /// are exactly what a human has to look at.
    /// </para>
    /// </summary>
    private static bool SurnameMatches(string read, string? recorded)
        => SearchKeyBuilder.NormalizeTerm(recorded) is { Length: > 0 } left
        && left == SearchKeyBuilder.NormalizeTerm(read);

    /// <summary>
    /// Every given name on the RECORD must appear among those READ on the document — not the
    /// reverse.
    ///
    /// <para>
    /// <b>Why it is asymmetric.</b> An identity document lists a person's full civil given names;
    /// a client record holds what they gave at the counter. "AWA" on the record against
    /// "AWA MARIE" on the card is the same person with one name omitted at capture, and routing
    /// every such file to a human would mean routing nearly all of them — the condition would stop
    /// discriminating. The reverse direction is the one that matters: a name on the record that is
    /// absent from the document means the document is not this person's, and that fails.
    /// </para>
    ///
    /// <para>
    /// Token-wise and order-insensitive, because the two sides disagree about order routinely —
    /// an MRZ puts the surname first and a record does not, and compound given names are entered
    /// in whichever order the agent heard them.
    /// </para>
    /// </summary>
    private static bool GivenNamesMatch(string read, string? recorded)
    {
        var recordedTokens = SearchKeyBuilder.NormalizeTerm(recorded)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // A record with no given name at all cannot be declared consistent: the comparison would
        // be vacuously true and the condition would pass on a file nothing was checked on.
        if (recordedTokens.Length == 0) return false;

        var readTokens = SearchKeyBuilder.NormalizeTerm(read)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        return recordedTokens.All(readTokens.Contains);
    }
}

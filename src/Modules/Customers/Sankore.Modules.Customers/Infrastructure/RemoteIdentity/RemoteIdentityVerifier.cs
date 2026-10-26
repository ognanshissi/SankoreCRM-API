namespace Sankore.Modules.Customers.Infrastructure.RemoteIdentity;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// Checks the two things a customer is asked for on the public remote-KYC page: their client
/// number and their date of birth (KRM-B-05).
///
/// <para>
/// <b>Why the comparison happens here.</b> The client number is M01's, the date of birth is
/// AES-256-GCM in M01's own column with an HMAC blind index beside it, and neither value may leave
/// this module — least of all towards the anonymous endpoint that is asking. So M02 asks the
/// question and gets a boolean; the values never cross the boundary in either direction.
/// </para>
///
/// <para>
/// <b>Why the date is compared on its blind index and not by decrypting.</b> The index is an HMAC
/// over the ISO form, which is exactly what equality on a date of birth needs, and it costs no
/// decryption at all — so the one path an anonymous caller can reach never holds the plaintext of
/// a regulated field, not even transiently in memory.
/// </para>
/// </summary>
// Public like ClientEmailNotifier next door: the facade is internal but the test project builds
// its doubles against this type, and the facade's constructor is reached from there.
public sealed class RemoteIdentityVerifier(
    CustomersDbContext db,
    IBlindIndexer indexer,
    ILogger<RemoteIdentityVerifier> logger)
{
    public async Task<bool> VerifyAsync(
        Guid tenantId,
        Guid clientId,
        string? clientNumber,
        DateOnly? birthDate,
        CancellationToken ct)
    {
        // IgnoreQueryFilters with an explicit tenant predicate: the caller is a public endpoint
        // with no ambient tenant of its own.
        var client = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.Id == clientId)
            .Select(c => new { c.ClientNumber, c.DateOfBirthBlindIndex })
            .FirstOrDefaultAsync(ct);

        // An unknown client, and a client whose date of birth was never recorded, are both NO.
        //
        // The second case is the one worth stating: a lead converted without a birth date produces
        // a client who cannot identify remotely at all. That is correct rather than a gap — there
        // is no second factor to check, and letting the client number alone through would turn a
        // number printed in an email into the whole credential.
        //
        // Both are checked BEFORE the comparison and short-circuit, which is a timing difference a
        // caller can measure. It tells them a client id they do not have and cannot choose is
        // unusable; what they can actually iterate on — the number and the date — is below the
        // short-circuit, and that is the part held to a fixed-time comparison.
        if (client is null || string.IsNullOrEmpty(client.DateOfBirthBlindIndex))
        {
            logger.LogInformation(
                "Remote identity check refused for client {ClientId}: unknown, or no recorded date of birth.",
                clientId);
            return false;
        }

        var numberMatches = FixedTimeEquals(
            NormalizeClientNumber(clientNumber),
            NormalizeClientNumber(client.ClientNumber));

        // Computed even when the number already failed, and the two results are combined with a
        // NON short-circuiting operator below: every attempt therefore costs one HMAC and two
        // comparisons whatever is wrong with it. Without that, "wrong number" would return
        // measurably faster than "right number, wrong date", which is the difference an attacker
        // needs to know they have found a real client number.
        var candidateIndex = birthDate is { } date
            ? indexer.Compute(
                BlindIndexPurpose.DateOfBirth, SensitiveValueNormalizer.NormalizeDateOfBirth(date))
            : string.Empty;

        var dateMatches = FixedTimeEquals(candidateIndex, client.DateOfBirthBlindIndex);

        // `&` and not `&&`: the right-hand side must be evaluated either way. It is already
        // computed above, but the operator is what stops a later edit from reintroducing the
        // short-circuit.
        return numberMatches & dateMatches;
    }

    /// <summary>
    /// The client number as the customer could plausibly type it from their welcome email: spacing
    /// dropped, case folded, and the separators of the <c>{AGENCY}-{YYYY}-{000000}</c> format left
    /// alone because they carry meaning.
    ///
    /// <para>
    /// Normalising both sides rather than only the input: the stored value is trimmed on write but
    /// nothing guarantees its casing across the formats the generator has had.
    /// </para>
    /// </summary>
    /// <summary>
    /// The client a number AND a date of birth designate together, or null — for a channel that
    /// starts from neither a link nor a known client: the WhatsApp complaint intake (2026-10-26).
    ///
    /// <para>
    /// The number finds a candidate, then <see cref="VerifyAsync"/> decides, so the date is compared
    /// the one fixed-time way. A number with no candidate still costs one HMAC, so « no such number »
    /// does not answer faster than « wrong date ». A merged record is no candidate: its number now
    /// belongs to the survivor's history, not to a person who can be answered.
    /// </para>
    /// </summary>
    public async Task<Guid?> IdentifyAsync(Guid tenantId, string? clientNumber, DateOnly birthDate, CancellationToken ct)
    {
        var normalized = NormalizeClientNumber(clientNumber);

        var candidate = normalized.Length == 0
            ? null
            : await db.Clients
                .IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId
                         && c.Status != ClientStatus.Merged
                         && c.ClientNumber.ToUpper().Replace(" ", "") == normalized)
                .Select(c => (Guid?)c.Id)
                .FirstOrDefaultAsync(ct);

        if (candidate is not { } clientId)
        {
            _ = indexer.Compute(BlindIndexPurpose.DateOfBirth, SensitiveValueNormalizer.NormalizeDateOfBirth(birthDate));
            return null;
        }

        return await VerifyAsync(tenantId, clientId, clientNumber, birthDate, ct) ? clientId : null;
    }

    private static string NormalizeClientNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var builder = new StringBuilder(raw.Length);

        foreach (var c in raw)
        {
            if (char.IsWhiteSpace(c)) continue;
            builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Comparison whose duration does not depend on WHERE the two values first differ.
    ///
    /// <para>
    /// <see cref="CryptographicOperations.FixedTimeEquals"/> over the UTF-8 bytes. It still leaks
    /// the LENGTH — the lengths are compared first and a mismatch returns immediately — and that is
    /// accepted deliberately: a client number's length is a property of the tenant's format, not a
    /// secret, and a blind index is always the same width. What must not leak is the position of
    /// the first differing character, which is what lets an attacker recover a value one character
    /// at a time.
    /// </para>
    /// </summary>
    private static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);

        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

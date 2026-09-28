namespace Sankore.Modules.Customers.Domain.Matching;

using System.Security.Cryptography;
using System.Text;

/// <summary>One weighted criterion evaluated while comparing two clients.</summary>
public sealed record MatchSignal(string Key, int Weight, bool Matched);

/// <summary>Total score (0-100) plus the signals that produced it, for the review screen.</summary>
public sealed record MatchScore(int Score, IReadOnlyList<MatchSignal> Signals);

/// <summary>
/// The comparable projection of a client. Deliberately contains NO clear personal data:
/// dates of birth and document numbers only ever travel as blind indexes, so the nightly
/// duplicate job can compare them without ever decrypting anything.
/// </summary>
public sealed record ClientMatchInput(
    Guid ClientId,
    string? PhoneticKeyPrimary,
    string? PhoneticKeySecondary,
    string? DateOfBirthBlindIndex,
    Guid AgencyId,
    string? FatherName,
    string? MotherName,
    string? IdentityDocumentNumberBlindIndex);

/// <summary>
/// Scores how likely two clients are the same person. Used by the nightly duplicate-detection
/// job, which keeps every pair whose score reaches
/// <see cref="CustomerSettingKeys.DuplicateScoreThreshold"/>.
/// <para>Weights (sum 130, score capped at 100):</para>
/// <list type="table">
///   <item><term>identity document blind index equal</term><description>60</description></item>
///   <item><term>both phonetic keys equal</term><description>25</description></item>
///   <item><term>one phonetic key equal</term><description>15</description></item>
///   <item><term>date of birth blind index equal</term><description>20</description></item>
///   <item><term>same agency</term><description>5</description></item>
///   <item><term>father name equal</term><description>10</description></item>
///   <item><term>mother name equal</term><description>10</description></item>
/// </list>
/// </summary>
public static class ClientMatchScorer
{
    public const string SignalIdentityDocument = "identity-document";
    public const string SignalPhoneticBoth = "phonetic-both";
    public const string SignalPhoneticOne = "phonetic-one";
    public const string SignalDateOfBirth = "date-of-birth";
    public const string SignalSameAgency = "same-agency";
    public const string SignalFatherName = "father-name";
    public const string SignalMotherName = "mother-name";

    public const int WeightIdentityDocument = 60;
    public const int WeightPhoneticBoth = 25;
    public const int WeightPhoneticOne = 15;
    public const int WeightDateOfBirth = 20;
    public const int WeightSameAgency = 5;
    public const int WeightFatherName = 10;
    public const int WeightMotherName = 10;

    /// <summary>Maximum score a pair can reach; the raw weights sum higher and are clamped here.</summary>
    public const int MaxScore = 100;

    public static MatchScore Score(ClientMatchInput a, ClientMatchInput b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var documentMatch = SameValue(a.IdentityDocumentNumberBlindIndex, b.IdentityDocumentNumberBlindIndex);

        var primaryMatch = SameValue(a.PhoneticKeyPrimary, b.PhoneticKeyPrimary);
        var secondaryMatch = SameValue(a.PhoneticKeySecondary, b.PhoneticKeySecondary);
        // Swapped first/last name still counts as a single key match.
        var crossMatch = SameValue(a.PhoneticKeyPrimary, b.PhoneticKeySecondary)
                         || SameValue(a.PhoneticKeySecondary, b.PhoneticKeyPrimary);

        var bothPhonetic = primaryMatch && secondaryMatch;
        var onePhonetic = !bothPhonetic && (primaryMatch || secondaryMatch || crossMatch);

        var signals = new List<MatchSignal>
        {
            new(SignalIdentityDocument, WeightIdentityDocument, documentMatch),
            new(SignalPhoneticBoth, WeightPhoneticBoth, bothPhonetic),
            new(SignalPhoneticOne, WeightPhoneticOne, onePhonetic),
            new(SignalDateOfBirth, WeightDateOfBirth, SameValue(a.DateOfBirthBlindIndex, b.DateOfBirthBlindIndex)),
            new(SignalSameAgency, WeightSameAgency, a.AgencyId != Guid.Empty && a.AgencyId == b.AgencyId),
            new(SignalFatherName, WeightFatherName, SameName(a.FatherName, b.FatherName)),
            new(SignalMotherName, WeightMotherName, SameName(a.MotherName, b.MotherName)),
        };

        var raw = 0;
        foreach (var signal in signals)
        {
            if (signal.Matched)
                raw += signal.Weight;
        }

        return new MatchScore(Math.Min(MaxScore, raw), signals);
    }

    /// <summary>
    /// Stable SHA-256 (lower-case hex) of the comparable fields only — never of the client id.
    /// Two candidate rows carrying the same pair of fingerprints describe the same comparison,
    /// so the nightly job can skip a pair a reviewer already rejected while still re-opening it
    /// as soon as one of the compared fields changes.
    /// </summary>
    public static string Fingerprint(ClientMatchInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var payload = string.Join(
            '|',
            input.PhoneticKeyPrimary ?? string.Empty,
            input.PhoneticKeySecondary ?? string.Empty,
            input.DateOfBirthBlindIndex ?? string.Empty,
            input.AgencyId.ToString("N"),
            NormalizeName(input.FatherName),
            NormalizeName(input.MotherName),
            input.IdentityDocumentNumberBlindIndex ?? string.Empty);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool SameValue(string? x, string? y) =>
        !string.IsNullOrWhiteSpace(x) && !string.IsNullOrWhiteSpace(y) && string.Equals(x, y, StringComparison.Ordinal);

    private static bool SameName(string? x, string? y)
    {
        if (string.IsNullOrWhiteSpace(x) || string.IsNullOrWhiteSpace(y))
            return false;

        return string.Equals(NormalizeName(x), NormalizeName(y), StringComparison.Ordinal);
    }

    private static string NormalizeName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : WestAfricanNameNormalizer.Normalize(value);
}

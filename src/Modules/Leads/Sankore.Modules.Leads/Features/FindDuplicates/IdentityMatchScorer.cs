namespace Sankore.Modules.Leads.Features.FindDuplicates;

using Sankore.Modules.Leads.Domain;

/// <summary>
/// Computes a 0-100 confidence score between a search probe and a candidate Lead
/// using multi-signal matching with weighted contributions.
///
/// Signal weights:
///   Phone (exact normalized)   55 pts
///   NationalId (exact)         55 pts
///   CustomerReference (exact)  50 pts
///   Email (exact, icase)       45 pts
///   DateOfBirth (exact)        20 pts
///   FullName (Jaro-Winkler)    0-25 pts
///   Location (proximity km)    0-12 pts
/// </summary>
internal sealed class IdentityMatchScorer
{
    // ── Thresholds ────────────────────────────────────────────────────────────

    /// <summary>Minimum confidence (inclusive) for a result to be returned.</summary>
    public const double MinConfidence = 30.0;

    private const double NameHighSimilarity   = 0.90;
    private const double NameMedSimilarity    = 0.80;
    private const double NameLowSimilarity    = 0.70;
    private const double LocationCloseKm      = 0.5;
    private const double LocationNearKm       = 2.0;

    // ── Score a single candidate ──────────────────────────────────────────────

    public DuplicateMatchResult? Score(Lead candidate, MatchProbe probe, double minConfidence = MinConfidence)
    {
        var reasons = new List<MatchReason>();
        double total = 0;

        // ── Phone ─────────────────────────────────────────────────────────────
        if (probe.PhoneDigits is not null)
        {
            var candidateDigits = NormalizePhone(candidate.PhoneNumber);
            if (PhoneMatches(candidateDigits, probe.PhoneDigits))
            {
                const double pts = 55;
                total += pts;
                reasons.Add(new MatchReason("phone", "Téléphone identique (normalisé)", pts, IsExact: true));
            }
        }

        // ── National ID ───────────────────────────────────────────────────────
        if (probe.NationalId is not null && candidate.NationalId is not null &&
            string.Equals(candidate.NationalId, probe.NationalId, StringComparison.OrdinalIgnoreCase))
        {
            const double pts = 55;
            total += pts;
            reasons.Add(new MatchReason("nationalId", "Numéro d'identité identique", pts, IsExact: true));
        }

        // ── Customer Reference ────────────────────────────────────────────────
        if (probe.CustomerReference is not null && candidate.CustomerReference is not null &&
            string.Equals(candidate.CustomerReference, probe.CustomerReference, StringComparison.OrdinalIgnoreCase))
        {
            const double pts = 50;
            total += pts;
            reasons.Add(new MatchReason("customerReference", "Référence client identique", pts, IsExact: true));
        }

        // ── Email ─────────────────────────────────────────────────────────────
        if (probe.EmailNorm is not null && candidate.Email is not null &&
            string.Equals(candidate.Email.Trim(), probe.EmailNorm, StringComparison.OrdinalIgnoreCase))
        {
            const double pts = 45;
            total += pts;
            reasons.Add(new MatchReason("email", "Email identique", pts, IsExact: true));
        }

        // ── Date of birth ─────────────────────────────────────────────────────
        if (probe.DateOfBirth is not null && candidate.DateOfBirth == probe.DateOfBirth)
        {
            const double pts = 20;
            total += pts;
            reasons.Add(new MatchReason("dateOfBirth", "Date de naissance identique", pts, IsExact: true));
        }

        // ── Full name (Jaro-Winkler) ──────────────────────────────────────────
        if (probe.FullNameNorm is not null)
        {
            var candidateName = candidate.FullName.Trim().ToLowerInvariant();
            var similarity = JaroWinkler(candidateName, probe.FullNameNorm);

            if (similarity >= NameHighSimilarity)
            {
                var pts = 25.0 * similarity;
                total += pts;
                var pct = (int)(similarity * 100);
                reasons.Add(new MatchReason("name", $"Nom très similaire ({pct}%)", pts, IsExact: false));
            }
            else if (similarity >= NameMedSimilarity)
            {
                var pts = 15.0 * similarity;
                total += pts;
                var pct = (int)(similarity * 100);
                reasons.Add(new MatchReason("name", $"Nom similaire ({pct}%)", pts, IsExact: false));
            }
            else if (similarity >= NameLowSimilarity)
            {
                var pts = 8.0 * similarity;
                total += pts;
                var pct = (int)(similarity * 100);
                reasons.Add(new MatchReason("name", $"Nom partiellement similaire ({pct}%)", pts, IsExact: false));
            }
        }

        // ── Location (Haversine) ──────────────────────────────────────────────
        if (probe.Latitude is not null && probe.Longitude is not null && candidate.Location is not null)
        {
            var distKm = HaversineKm(
                probe.Latitude.Value, probe.Longitude.Value,
                candidate.Location.Latitude, candidate.Location.Longitude);

            if (distKm <= LocationCloseKm)
            {
                const double pts = 12;
                total += pts;
                reasons.Add(new MatchReason("location", $"Localisation très proche ({distKm:F1} km)", pts, IsExact: false));
            }
            else if (distKm <= LocationNearKm)
            {
                const double pts = 6;
                total += pts;
                reasons.Add(new MatchReason("location", $"Localisation proche ({distKm:F1} km)", pts, IsExact: false));
            }
        }

        if (reasons.Count == 0 || total < minConfidence)
            return null;

        var confidence = Math.Min(100.0, Math.Round(total, 1));
        var label      = ConfidenceLabel(confidence);
        var summary    = BuildSummary(confidence, reasons);

        return new DuplicateMatchResult(
            LeadId:            candidate.Id,
            FullName:          candidate.FullName,
            PhoneNumber:       candidate.PhoneNumber,
            Email:             candidate.Email,
            NationalId:        candidate.NationalId,
            CustomerReference: candidate.CustomerReference,
            Status:            candidate.Status,
            Source:            candidate.Source,
            CapturedAt:        candidate.CapturedAt,
            ConfidenceScore:   confidence,
            ConfidenceLabel:   label,
            ConfidenceSummary: summary,
            MatchReasons:      reasons);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool PhoneMatches(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        // Match if one is a suffix of the other (handles country code variants)
        return a == b || a.EndsWith(b) || b.EndsWith(a);
    }

    internal static string NormalizePhone(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00", StringComparison.Ordinal)) digits = digits[2..];
        // Take the last 8 significant digits (local number portion)
        return digits.Length > 8 ? digits[^8..] : digits;
    }

    private static string ConfidenceLabel(double score) => score switch
    {
        >= 90 => "Très probable",
        >= 70 => "Probable",
        >= 50 => "Possible",
        _     => "Faible"
    };

    private static string BuildSummary(double confidence, List<MatchReason> reasons)
    {
        var topReasons = reasons
            .OrderByDescending(r => r.Contribution)
            .Take(2)
            .Select(r => r.Description);

        return $"{(int)confidence}% — {string.Join(" + ", topReasons)}";
    }

    // ── Jaro-Winkler similarity ───────────────────────────────────────────────

    private static double JaroWinkler(string s1, string s2)
    {
        if (s1 == s2) return 1.0;
        if (s1.Length == 0 || s2.Length == 0) return 0.0;

        int matchDist = Math.Max(Math.Max(s1.Length, s2.Length) / 2 - 1, 0);

        var s1Matches = new bool[s1.Length];
        var s2Matches = new bool[s2.Length];

        int matches = 0;
        for (int i = 0; i < s1.Length; i++)
        {
            int lo = Math.Max(0, i - matchDist);
            int hi = Math.Min(i + matchDist + 1, s2.Length);
            for (int j = lo; j < hi; j++)
            {
                if (s2Matches[j] || s1[i] != s2[j]) continue;
                s1Matches[i] = true;
                s2Matches[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0) return 0.0;

        int transpositions = 0;
        int k = 0;
        for (int i = 0; i < s1.Length; i++)
        {
            if (!s1Matches[i]) continue;
            while (!s2Matches[k]) k++;
            if (s1[i] != s2[k]) transpositions++;
            k++;
        }

        double jaro = (matches / (double)s1.Length
                     + matches / (double)s2.Length
                     + (matches - transpositions / 2.0) / matches) / 3.0;

        // Winkler prefix bonus (up to 4 chars)
        int prefix = 0;
        int prefixLimit = Math.Min(4, Math.Min(s1.Length, s2.Length));
        for (int i = 0; i < prefixLimit; i++)
        {
            if (s1[i] == s2[i]) prefix++;
            else break;
        }

        return jaro + prefix * 0.1 * (1 - jaro);
    }

    // ── Haversine distance (km) ───────────────────────────────────────────────

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2))
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}

/// <summary>All search signals normalised into a single probe object.</summary>
internal sealed record MatchProbe(
    string? PhoneDigits,
    string? EmailNorm,
    string? NationalId,
    string? CustomerReference,
    string? FullNameNorm,
    DateOnly? DateOfBirth,
    double? Latitude,
    double? Longitude)
{
    public static MatchProbe From(
        string? phone, string? email, string? nationalId,
        string? customerReference, string? fullName,
        DateOnly? dateOfBirth, double? latitude, double? longitude)
        => new(
            PhoneDigits:       string.IsNullOrWhiteSpace(phone)             ? null : IdentityMatchScorer.NormalizePhone(phone.Trim()),
            EmailNorm:         string.IsNullOrWhiteSpace(email)             ? null : email.Trim().ToLowerInvariant(),
            NationalId:        string.IsNullOrWhiteSpace(nationalId)        ? null : nationalId.Trim(),
            CustomerReference: string.IsNullOrWhiteSpace(customerReference) ? null : customerReference.Trim(),
            FullNameNorm:      string.IsNullOrWhiteSpace(fullName)          ? null : fullName.Trim().ToLowerInvariant(),
            DateOfBirth:       dateOfBirth,
            Latitude:          latitude,
            Longitude:         longitude);
}

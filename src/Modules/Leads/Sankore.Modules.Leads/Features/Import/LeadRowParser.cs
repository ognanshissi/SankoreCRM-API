namespace Sankore.Modules.Leads.Features.Import;

using System.Globalization;
using Sankore.Modules.Leads.Domain;

/// <summary>Typed row ready to be dispatched as a <c>CaptureLeadCommand</c>.</summary>
public sealed record ParsedLeadRow(
    string FullName,
    string PhoneNumber,
    LeadSource Source,
    string InterestedProduct,
    string PreferredLanguage,
    double Latitude,
    double Longitude,
    string? FirstName,
    string? LastName,
    string? Email,
    string? NationalId,
    LeadGender Gender,
    DateOnly? DateOfBirth,
    decimal? DesiredAmount,
    string? DesiredCurrency,
    string? Campaign,
    LeadChannel? Channel,
    string? Comment,
    string? ExternalReference,
    string? CompanyName,
    Guid? OwnerId,
    Guid? AgencyId);

public sealed record ParsedRowResult(ParsedLeadRow? Row, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0 && Row is not null;
}

/// <summary>
/// Converts a raw <see cref="ImportLeadRow"/> into typed values, collecting every
/// problem for the row instead of throwing — one bad cell must fail its own row,
/// never the whole import. Blank fields fall back to the job's
/// <see cref="ImportDefaults"/>, then to the documented defaults below.
/// </summary>
public static class LeadRowParser
{
    private const string FallbackLanguage = "FR";

    public static ParsedRowResult Parse(
        ImportLeadRow raw, ImportDefaults defaults, LeadSource fallbackSource)
    {
        var errors = new List<string>();

        // ── Name: FullName, or composed from first + last ────────────────
        var fullName = Clean(raw.FullName);
        if (fullName is null)
        {
            var composed = string.Join(' ',
                new[] { Clean(raw.FirstName), Clean(raw.LastName) }.Where(p => p is not null));
            fullName = string.IsNullOrWhiteSpace(composed) ? null : composed;
        }

        if (fullName is null)
            errors.Add("FullName is required (or FirstName + LastName).");

        // ── Phone ────────────────────────────────────────────────────────
        var phone = Clean(raw.PhoneNumber);
        if (phone is null)
            errors.Add("PhoneNumber is required.");

        // ── Product: row → job defaults → error ──────────────────────────
        var product = Clean(raw.InterestedProduct) ?? Clean(defaults.InterestedProduct);
        if (product is null)
            errors.Add("InterestedProduct is required (set it on the row or as an import default).");

        var language = Clean(raw.PreferredLanguage)
                    ?? Clean(defaults.PreferredLanguage)
                    ?? FallbackLanguage;

        // ── Source ───────────────────────────────────────────────────────
        var source = defaults.Source ?? fallbackSource;
        var rawSource = Clean(raw.Source);
        if (rawSource is not null)
        {
            if (Enum.TryParse<LeadSource>(rawSource, ignoreCase: true, out var parsed))
                source = parsed;
            else
                errors.Add($"Source '{rawSource}' is not a known lead source.");
        }

        // ── Coordinates: blank means "unknown", which is 0/0 ─────────────
        var latitude  = ParseDouble(raw.Latitude, "Latitude", -90, 90, errors);
        var longitude = ParseDouble(raw.Longitude, "Longitude", -180, 180, errors);

        // ── Optional typed fields ────────────────────────────────────────
        var gender = LeadGender.Unknown;
        var rawGender = Clean(raw.Gender);
        if (rawGender is not null)
        {
            if (Enum.TryParse<LeadGender>(rawGender, ignoreCase: true, out var g))
                gender = g;
            else
                errors.Add($"Gender '{rawGender}' is not valid.");
        }

        LeadChannel? channel = null;
        var rawChannel = Clean(raw.Channel);
        if (rawChannel is not null)
        {
            if (Enum.TryParse<LeadChannel>(rawChannel, ignoreCase: true, out var c))
                channel = c;
            else
                errors.Add($"Channel '{rawChannel}' is not valid.");
        }

        DateOnly? dateOfBirth = null;
        var rawDob = Clean(raw.DateOfBirth);
        if (rawDob is not null)
        {
            if (DateOnly.TryParse(rawDob, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                dateOfBirth = d;
            else
                errors.Add($"DateOfBirth '{rawDob}' is not a valid date (expected yyyy-MM-dd).");
        }

        decimal? desiredAmount = null;
        var rawAmount = Clean(raw.DesiredAmount);
        if (rawAmount is not null)
        {
            if (decimal.TryParse(rawAmount, NumberStyles.Any, CultureInfo.InvariantCulture, out var a))
                desiredAmount = a;
            else
                errors.Add($"DesiredAmount '{rawAmount}' is not a number.");
        }

        var currency = Clean(raw.DesiredCurrency);
        if (desiredAmount.HasValue && currency is null)
            errors.Add("DesiredCurrency is required when DesiredAmount is set.");

        var ownerId  = ParseGuid(raw.OwnerId, "OwnerId", errors);
        var agencyId = ParseGuid(raw.AgencyId, "AgencyId", errors);

        if (errors.Count > 0)
            return new ParsedRowResult(null, errors);

        return new ParsedRowResult(
            new ParsedLeadRow(
                FullName:          fullName!,
                PhoneNumber:       phone!,
                Source:            source,
                InterestedProduct: product!,
                PreferredLanguage: language,
                Latitude:          latitude,
                Longitude:         longitude,
                FirstName:         Clean(raw.FirstName),
                LastName:          Clean(raw.LastName),
                Email:             Clean(raw.Email),
                NationalId:        Clean(raw.NationalId),
                Gender:            gender,
                DateOfBirth:       dateOfBirth,
                DesiredAmount:     desiredAmount,
                DesiredCurrency:   currency,
                Campaign:          Clean(raw.Campaign),
                Channel:           channel,
                Comment:           Clean(raw.Comment),
                ExternalReference: Clean(raw.ExternalReference),
                CompanyName:       Clean(raw.CompanyName),
                OwnerId:           ownerId,
                AgencyId:          agencyId),
            []);
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static double ParseDouble(
        string? value, string field, double min, double max, List<string> errors)
    {
        var raw = Clean(value);
        if (raw is null) return 0;

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            errors.Add($"{field} '{raw}' is not a number.");
            return 0;
        }

        if (parsed < min || parsed > max)
        {
            errors.Add($"{field} '{raw}' is out of range ({min} to {max}).");
            return 0;
        }

        return parsed;
    }

    private static Guid? ParseGuid(string? value, string field, List<string> errors)
    {
        var raw = Clean(value);
        if (raw is null) return null;

        if (Guid.TryParse(raw, out var parsed)) return parsed;

        errors.Add($"{field} '{raw}' is not a valid identifier.");
        return null;
    }
}

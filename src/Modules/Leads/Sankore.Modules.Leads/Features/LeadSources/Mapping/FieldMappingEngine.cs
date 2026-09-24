namespace Sankore.Modules.Leads.Features.LeadSources.Mapping;

using Newtonsoft.Json.Linq;
using Sankore.Modules.Leads.Domain;

/// <summary>
/// Unified field mapping engine (US-F13.37-BE-07).
/// Extracts values from a raw JSON payload via JSONPath, applies transformations,
/// and maps them to Lead target fields. Used by all integration modes.
/// </summary>
internal static class FieldMappingEngine
{
    /// <summary>
    /// Known Lead target fields that can appear as a rule's TargetField.
    /// </summary>
    public static readonly HashSet<string> ValidTargetFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "fullName", "firstName", "lastName", "phoneNumber", "email",
        "interestedProduct", "preferredLanguage", "nationalId", "customerReference",
        "comment", "campaign", "externalReference", "externalId",
        "utmSource", "utmMedium", "utmCampaign", "utmContent", "utmTerm",
        "landingPage", "referrer", "socialPublicationId", "socialInteractionId",
        "dateOfBirth", "gender", "prospectType",
        "latitude", "longitude",
        "desiredAmount", "desiredCurrency",
    };

    /// <summary>
    /// Validates a set of mapping rules. Returns errors keyed by source field.
    /// </summary>
    public static IReadOnlyList<MappingValidationError> Validate(
        IReadOnlyList<FieldMappingRule> rules)
    {
        var errors = new List<MappingValidationError>();

        if (rules.Count == 0)
        {
            errors.Add(new MappingValidationError(
                "(rules)", "At least one mapping rule is required."));
            return errors;
        }

        foreach (var rule in rules)
        {
            var path = string.IsNullOrWhiteSpace(rule.SourceField) ? "(empty)" : rule.SourceField;

            if (string.IsNullOrWhiteSpace(rule.SourceField))
                errors.Add(new MappingValidationError(path, "sourceField is required."));
            else if (!IsParseableJsonPath(rule.SourceField))
                errors.Add(new MappingValidationError(path, $"Invalid JSONPath: '{rule.SourceField}'."));

            if (string.IsNullOrWhiteSpace(rule.TargetField))
                errors.Add(new MappingValidationError(path, "targetField is required."));
            else if (!ValidTargetFields.Contains(rule.TargetField))
                errors.Add(new MappingValidationError(path, $"Unknown target field: '{rule.TargetField}'."));

            switch (rule.Transformation)
            {
                case FieldTransformation.E164 when ResolveCountryPrefix(rule.E164Country) is null:
                    errors.Add(new MappingValidationError(path,
                        $"e164Country '{rule.E164Country}' is not a known ISO-3166 alpha-2 code or dial prefix."));
                    break;

                case FieldTransformation.Map when rule.MapEntries is null or { Count: 0 }:
                    errors.Add(new MappingValidationError(path,
                        "mapEntries is required for the 'map' transformation."));
                    break;
            }
        }

        // A target may be fed by several rules only when they concatenate.
        var duplicates = rules
            .Where(r => !string.IsNullOrWhiteSpace(r.TargetField))
            .GroupBy(r => r.TargetField, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in duplicates)
        {
            if (group.Any(r => r.Transformation != FieldTransformation.Concat))
            {
                errors.Add(new MappingValidationError(group.Key,
                    $"Target field '{group.Key}' is mapped {group.Count()} times; " +
                    "use the 'concat' transformation on each rule to combine them."));
            }
        }

        errors.AddRange(ValidateRequiredTargets(rules));

        return errors;
    }

    /// <summary>
    /// A lead needs a phone number and a name. The name may be mapped directly as
    /// fullName, or built from firstName + lastName.
    /// </summary>
    private static IEnumerable<MappingValidationError> ValidateRequiredTargets(
        IReadOnlyList<FieldMappingRule> rules)
    {
        var targets = new HashSet<string>(
            rules.Where(r => !string.IsNullOrWhiteSpace(r.TargetField)).Select(r => r.TargetField),
            StringComparer.OrdinalIgnoreCase);

        if (!targets.Contains("phoneNumber"))
        {
            yield return new MappingValidationError(
                "(missing:phoneNumber)", "Required target field 'phoneNumber' is not mapped.");
        }

        if (!targets.Contains("fullName")
            && !(targets.Contains("firstName") && targets.Contains("lastName")))
        {
            yield return new MappingValidationError(
                "(missing:fullName)",
                "A name is required: map 'fullName', or map both 'firstName' and 'lastName'.");
        }
    }

    /// <summary>
    /// Applies mapping rules to a raw JSON payload, returning extracted values
    /// and per-field errors. Several rules targeting the same field are joined
    /// using the later rule's ConcatSeparator (default: a single space).
    /// </summary>
    public static MappingResult Apply(
        IReadOnlyList<FieldMappingRule> rules,
        string rawPayloadJson)
    {
        JObject payload;
        try
        {
            payload = JObject.Parse(rawPayloadJson);
        }
        catch (Exception ex)
        {
            return new MappingResult(
                new Dictionary<string, string?>(),
                [new MappingFieldError("(root)", $"Invalid JSON: {ex.Message}")]);
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var fieldErrors = new List<MappingFieldError>();

        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.SourceField)
                || string.IsNullOrWhiteSpace(rule.TargetField))
            {
                continue;
            }

            JToken? token;
            try
            {
                token = payload.SelectToken(rule.SourceField);
            }
            catch (Exception ex)
            {
                fieldErrors.Add(new MappingFieldError(rule.SourceField, $"JSONPath error: {ex.Message}"));
                continue;
            }

            var raw = token is null || token.Type == JTokenType.Null ? null : token.ToString();

            // Fall back to the rule's default when the payload has nothing usable
            if (string.IsNullOrEmpty(raw))
                raw = rule.DefaultValue;

            // Still nothing — leave the target unset (nullable fields stay null)
            if (string.IsNullOrEmpty(raw))
                continue;

            var (value, error) = ApplyTransform(raw, rule);
            if (error is not null)
            {
                fieldErrors.Add(new MappingFieldError(rule.SourceField, error));
                continue;
            }

            if (value is null)
                continue;

            values[rule.TargetField] =
                values.TryGetValue(rule.TargetField, out var existing) && !string.IsNullOrEmpty(existing)
                    ? existing + (rule.ConcatSeparator ?? " ") + value
                    : value;
        }

        return new MappingResult(values, fieldErrors);
    }

    private static (string? Value, string? Error) ApplyTransform(
        string value, FieldMappingRule rule)
        => rule.Transformation switch
        {
            FieldTransformation.None   => (value, null),
            FieldTransformation.Trim   => (value.Trim(), null),
            FieldTransformation.Upper  => (value.Trim().ToUpperInvariant(), null),
            FieldTransformation.Lower  => (value.Trim().ToLowerInvariant(), null),
            FieldTransformation.Concat => (value.Trim(), null),
            FieldTransformation.E164   => NormalizeE164(value, rule.E164Country ?? ""),
            FieldTransformation.Map    => (LookupMapEntry(value, rule), null),
            _ => (null, $"Unknown transformation: '{rule.Transformation}'.")
        };

    /// <summary>
    /// Translates a value through the rule's lookup table. Unmatched values fall back
    /// to DefaultValue when set, otherwise pass through unchanged.
    /// </summary>
    private static string LookupMapEntry(string value, FieldMappingRule rule)
    {
        if (rule.MapEntries is null) return value;

        foreach (var (key, mapped) in rule.MapEntries)
        {
            if (string.Equals(key, value, StringComparison.OrdinalIgnoreCase))
                return mapped;
        }

        return rule.DefaultValue ?? value;
    }

    private static bool IsParseableJsonPath(string jsonPath)
    {
        try
        {
            JObject.Parse("{}").SelectToken(jsonPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Normalizes a phone number to E.164 format.
    /// <paramref name="country"/> accepts an ISO 3166-1 alpha-2 code ("CI") or a
    /// dial prefix ("+225" / "225"). Returns an error if the number is invalid.
    /// </summary>
    internal static (string? Phone, string? Error) NormalizeE164(string raw, string country)
    {
        // Strip non-digit characters (except leading +)
        var digits = raw.StartsWith('+') ? "+" + DigitsOnly(raw[1..]) : DigitsOnly(raw);

        if (digits.StartsWith('+'))
        {
            // Already has country code — validate length
            var digitsPart = digits[1..];
            if (digitsPart.Length < 7 || digitsPart.Length > 15)
                return (null, $"InvalidPhone: '{raw}' has invalid length after E.164 normalization.");
            return (digits, null);
        }

        var prefix = ResolveCountryPrefix(country);
        if (prefix is null)
            return (null, $"InvalidPhone: unknown country code '{country}'.");

        // Remove leading 0 (local format)
        if (digits.StartsWith('0'))
            digits = digits[1..];

        var e164 = $"+{prefix}{digits}";

        var finalDigits = e164[1..];
        if (finalDigits.Length < 7 || finalDigits.Length > 15)
            return (null, $"InvalidPhone: '{raw}' has invalid length after E.164 normalization.");

        return (e164, null);
    }

    /// <summary>
    /// Resolves a country designation to a dial prefix. Accepts an ISO 3166-1 alpha-2
    /// code ("CI") or an already-explicit prefix ("+225" / "225"). Null when unknown.
    /// </summary>
    internal static string? ResolveCountryPrefix(string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return null;

        var trimmed = country.Trim();

        if (trimmed.StartsWith('+') || trimmed.All(char.IsDigit))
        {
            var digits = DigitsOnly(trimmed);
            return digits.Length is >= 1 and <= 4 ? digits : null;
        }

        return GetCountryPrefix(trimmed.ToUpperInvariant());
    }

    private static string DigitsOnly(string s) => new(s.Where(char.IsDigit).ToArray());

    private static string? GetCountryPrefix(string iso2) => iso2 switch
    {
        "CI" => "225",  // Côte d'Ivoire
        "SN" => "221",  // Sénégal
        "ML" => "223",  // Mali
        "BF" => "226",  // Burkina Faso
        "GN" => "224",  // Guinée
        "TG" => "228",  // Togo
        "BJ" => "229",  // Bénin
        "NE" => "227",  // Niger
        "CM" => "237",  // Cameroun
        "GA" => "241",  // Gabon
        "CG" => "242",  // Congo
        "CD" => "243",  // RDC
        "MG" => "261",  // Madagascar
        "MA" => "212",  // Maroc
        "TN" => "216",  // Tunisie
        "DZ" => "213",  // Algérie
        "FR" => "33",   // France
        "US" => "1",    // USA
        "GB" => "44",   // UK
        _ => null
    };
}

public sealed record MappingValidationError(string Path, string Message);

public sealed record MappingFieldError(string JsonPath, string Message);

public sealed record MappingResult(
    IReadOnlyDictionary<string, string?> MappedValues,
    IReadOnlyList<MappingFieldError> Errors);

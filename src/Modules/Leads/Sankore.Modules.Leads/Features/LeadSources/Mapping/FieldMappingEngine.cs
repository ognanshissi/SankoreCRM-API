namespace Sankore.Modules.Leads.Features.LeadSources.Mapping;

using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

/// <summary>
/// Unified field mapping engine (US-F13.37-BE-07).
/// Extracts values from a raw JSON payload via JSONPath, applies transformations,
/// and maps them to Lead target fields. Used by all integration modes.
/// </summary>
internal static class FieldMappingEngine
{
    /// <summary>
    /// Known Lead target fields that can appear as values in a FieldMapping.
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
    /// Required target fields that MUST be present in a mapping for ingestion to succeed.
    /// </summary>
    public static readonly HashSet<string> RequiredTargetFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "phoneNumber", "fullName"
    };

    /// <summary>
    /// Validates a FieldMapping dictionary. Returns errors indexed by key.
    /// </summary>
    public static IReadOnlyList<MappingValidationError> Validate(
        IReadOnlyDictionary<string, string> fieldMapping)
    {
        var errors = new List<MappingValidationError>();

        // Check required target fields are present as values
        foreach (var required in RequiredTargetFields)
        {
            if (!fieldMapping.Values.Any(v => ExtractTargetField(v)
                    .Equals(required, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(new MappingValidationError(
                    $"(missing:{required})",
                    $"Required target field '{required}' is not mapped."));
            }
        }

        foreach (var (jsonPath, mappingExpr) in fieldMapping)
        {
            // Validate JSONPath is parseable
            try
            {
                var token = JObject.Parse("{}").SelectToken(jsonPath);
                // SelectToken on empty object returns null — that's fine, just check it doesn't throw
            }
            catch
            {
                errors.Add(new MappingValidationError(jsonPath, $"Invalid JSONPath: '{jsonPath}'."));
                continue;
            }

            // Validate mapping expression: "targetField" or "targetField|transform1|transform2"
            var parts = mappingExpr.Split('|');
            var target = parts[0].Trim();

            if (!ValidTargetFields.Contains(target))
            {
                errors.Add(new MappingValidationError(jsonPath,
                    $"Unknown target field: '{target}'."));
                continue;
            }

            // Validate each transformation
            for (int i = 1; i < parts.Length; i++)
            {
                var transform = parts[i].Trim();
                if (!IsValidTransform(transform))
                {
                    errors.Add(new MappingValidationError(jsonPath,
                        $"Invalid transformation: '{transform}'. Allowed: trim, e164:{{ISO2}}, map, concat."));
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Applies a FieldMapping to a raw JSON payload, returning extracted values
    /// and per-field errors.
    /// </summary>
    public static MappingResult Apply(
        IReadOnlyDictionary<string, string> fieldMapping,
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

        foreach (var (jsonPath, mappingExpr) in fieldMapping)
        {
            var parts = mappingExpr.Split('|');
            var targetField = parts[0].Trim();
            var transforms = parts.Skip(1).Select(t => t.Trim()).ToList();

            // Extract value via JSONPath
            JToken? token;
            try
            {
                token = payload.SelectToken(jsonPath);
            }
            catch (Exception ex)
            {
                fieldErrors.Add(new MappingFieldError(jsonPath, $"JSONPath error: {ex.Message}"));
                continue;
            }

            if (token is null || token.Type == JTokenType.Null)
            {
                // Not found — leave target unset (nullable fields stay null)
                continue;
            }

            var rawValue = token.ToString();

            // Apply transformations in order
            var (result, error) = ApplyTransforms(rawValue, transforms);
            if (error is not null)
            {
                fieldErrors.Add(new MappingFieldError(jsonPath, error));
                continue;
            }

            values[targetField] = result;
        }

        return new MappingResult(values, fieldErrors);
    }

    private static (string? Value, string? Error) ApplyTransforms(
        string value, IReadOnlyList<string> transforms)
    {
        var current = value;

        foreach (var transform in transforms)
        {
            if (transform.Equals("trim", StringComparison.OrdinalIgnoreCase))
            {
                current = current.Trim();
            }
            else if (transform.StartsWith("e164:", StringComparison.OrdinalIgnoreCase))
            {
                var iso2 = transform[5..].Trim().ToUpperInvariant();
                var (phone, err) = NormalizeE164(current, iso2);
                if (err is not null) return (null, err);
                current = phone!;
            }
            else if (transform.Equals("map", StringComparison.OrdinalIgnoreCase))
            {
                // Identity pass-through; enrichment rules could be added later
            }
            else if (transform.Equals("concat", StringComparison.OrdinalIgnoreCase))
            {
                // Concat is a no-op on single values; multi-field concat handled externally
            }
            else
            {
                return (null, $"Unknown transformation: '{transform}'.");
            }
        }

        return (current, null);
    }

    /// <summary>
    /// Normalizes a phone number to E.164 format given an ISO 3166-1 alpha-2 country code.
    /// Returns an error if the number is invalid.
    /// </summary>
    internal static (string? Phone, string? Error) NormalizeE164(string raw, string iso2)
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

        // Resolve country prefix from ISO2
        var prefix = GetCountryPrefix(iso2);
        if (prefix is null)
            return (null, $"InvalidPhone: unknown country code '{iso2}'.");

        // Remove leading 0 (local format)
        if (digits.StartsWith('0'))
            digits = digits[1..];

        var e164 = $"+{prefix}{digits}";

        var finalDigits = e164[1..];
        if (finalDigits.Length < 7 || finalDigits.Length > 15)
            return (null, $"InvalidPhone: '{raw}' has invalid length after E.164 normalization.");

        return (e164, null);
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

    private static bool IsValidTransform(string transform)
    {
        if (transform.Equals("trim", StringComparison.OrdinalIgnoreCase)) return true;
        if (transform.Equals("map", StringComparison.OrdinalIgnoreCase)) return true;
        if (transform.Equals("concat", StringComparison.OrdinalIgnoreCase)) return true;
        if (transform.StartsWith("e164:", StringComparison.OrdinalIgnoreCase))
        {
            var iso2 = transform[5..].Trim();
            return iso2.Length == 2;
        }
        return false;
    }

    private static string ExtractTargetField(string mappingExpr)
        => mappingExpr.Split('|')[0].Trim();
}

public sealed record MappingValidationError(string Path, string Message);

public sealed record MappingFieldError(string JsonPath, string Message);

public sealed record MappingResult(
    IReadOnlyDictionary<string, string?> MappedValues,
    IReadOnlyList<MappingFieldError> Errors);

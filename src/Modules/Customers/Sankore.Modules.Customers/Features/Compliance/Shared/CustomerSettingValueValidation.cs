namespace Sankore.Modules.Customers.Features.Compliance.Shared;

using System.Globalization;
using System.Text.Json;
using Sankore.Modules.Customers.Domain;

/// <summary>
/// Single place where a tenant setting's raw string value is checked against the
/// <c>ValueType</c> declared in <see cref="CustomerSettingKeys.Defaults"/>.
/// <para>
/// Shared by the validator (so the caller gets a 400 with a readable message) and by the
/// handler (defensive re-check, because a job or another module could dispatch the command
/// without going through the HTTP pipeline).
/// </para>
/// </summary>
internal static class CustomerSettingValueValidation
{
    /// <summary>
    /// Regulatory floor on <see cref="CustomerSettingKeys.RetentionYears"/>: a tenant may
    /// lengthen the retention period but never shorten it below ten years, otherwise the
    /// anonymization endpoint would let a client be erased before the legal term.
    /// </summary>
    public const int MinimumRetentionYears = 10;

    /// <summary>The declared default of <paramref name="key"/>, or <c>null</c> when unknown.</summary>
    public static CustomerSettingDefault? FindDefault(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        var normalized = key.Trim();

        return CustomerSettingKeys.Defaults
            .FirstOrDefault(d => string.Equals(d.Key, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsKnownKey(string? key) => FindDefault(key) is not null;

    /// <summary>
    /// Checks <paramref name="value"/> against the declared type of <paramref name="key"/>.
    /// Returns <c>null</c> when the value is acceptable, otherwise a human-readable reason.
    /// An unknown key is NOT reported here — the handler answers <c>SETTING_UNKNOWN</c> for that.
    /// </summary>
    public static string? Validate(string? key, string? value)
    {
        var declared = FindDefault(key);
        if (declared is null) return null;

        if (value is null)
            return "Value is required.";

        var trimmed = value.Trim();

        switch (declared.ValueType)
        {
            case CustomerSettingKeys.TypeInt:
                if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var asInt))
                    return $"'{declared.Key}' expects an integer.";
                return ValidateIntRange(declared.Key, asInt);

            case CustomerSettingKeys.TypeBool:
                return bool.TryParse(trimmed, out _)
                    ? null
                    : $"'{declared.Key}' expects 'true' or 'false'.";

            case CustomerSettingKeys.TypeDecimal:
                // Deliberately NOT NumberStyles.Number: that allows thousands separators, so a
                // French-keyboard "25,5" would parse as 255 under InvariantCulture and silently
                // multiply a threshold by ten. Only sign and '.' are accepted.
                const NumberStyles decimalStyles =
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

                return decimal.TryParse(trimmed, decimalStyles, CultureInfo.InvariantCulture, out _)
                    ? null
                    : $"'{declared.Key}' expects a decimal number using '.' as the separator.";

            case CustomerSettingKeys.TypeJson:
                try
                {
                    using var _ = JsonDocument.Parse(trimmed);
                    return null;
                }
                catch (JsonException ex)
                {
                    return $"'{declared.Key}' expects valid JSON: {ex.Message}";
                }

            case CustomerSettingKeys.TypeString:
            default:
                return trimmed.Length == 0 ? $"'{declared.Key}' cannot be empty." : null;
        }
    }

    /// <summary>
    /// True when <paramref name="value"/> parses but breaks a hard floor attached to the key
    /// (as opposed to simply not being of the right type).
    /// </summary>
    public static bool IsOutOfRange(string? key, string? value)
    {
        var declared = FindDefault(key);
        if (declared is null || value is null) return false;
        if (declared.ValueType != CustomerSettingKeys.TypeInt) return false;

        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var asInt)
            && ValidateIntRange(declared.Key, asInt) is not null;
    }

    private static string? ValidateIntRange(string key, int value)
    {
        if (string.Equals(key, CustomerSettingKeys.RetentionYears, StringComparison.OrdinalIgnoreCase)
            && value < MinimumRetentionYears)
        {
            return $"'{key}' cannot be lower than {MinimumRetentionYears} years " +
                   "(regulatory archive-retention floor).";
        }

        return value < 0 ? $"'{key}' cannot be negative." : null;
    }
}

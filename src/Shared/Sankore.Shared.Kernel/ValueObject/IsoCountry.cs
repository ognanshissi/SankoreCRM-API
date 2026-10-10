namespace Sankore.Shared.Kernel.ValueObject;

using System.Globalization;

/// <summary>
/// ISO 3166-1 alpha-2 country codes, derived from the runtime's own region data rather than from
/// a table checked into this repository — a hand-maintained list of countries is a list that goes
/// stale silently.
///
/// <para>
/// Two levels of check, and they are not interchangeable. <see cref="HasValidShape"/> asks only
/// whether the string could be a code (two ASCII letters) and is what the value objects enforce,
/// because a value object must not fail on a country the host's ICU data happens not to carry.
/// <see cref="IsKnown"/> asks whether it IS one and is what the validators use, where a wrong
/// country is worth a 400.
/// </para>
/// </summary>
public static class IsoCountry
{
    /// <summary>
    /// Built once from the specific cultures the runtime knows. Cultures that carry no region
    /// (invariant, neutral, or an ICU entry without a country) are skipped rather than throwing:
    /// <see cref="RegionInfo"/> rejects them, and one bad entry must not empty the whole set.
    /// </summary>
    private static readonly HashSet<string> Known = BuildKnown();

    private static HashSet<string> BuildKnown()
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                codes.Add(new RegionInfo(culture.Name).TwoLetterISORegionName.ToUpperInvariant());
            }
            catch (ArgumentException)
            {
                // Not a region-bearing culture on this host's ICU build. Skip it.
            }
        }

        return codes;
    }

    /// <summary>Two ASCII letters. Says nothing about the code designating a real country.</summary>
    public static bool HasValidShape(string? code)
        => code is { Length: 2 }
           && char.IsAsciiLetter(code[0])
           && char.IsAsciiLetter(code[1]);

    /// <summary>
    /// Whether the code is an ISO 3166-1 alpha-2 country this runtime knows. Case-insensitive.
    ///
    /// <para>
    /// Falls back to <see cref="HasValidShape"/> when the runtime carries no region data at all
    /// (a globalization-invariant deployment, where <see cref="Known"/> comes back empty). Failing
    /// closed there would reject every country on a host configured in a way this value object has
    /// no business having an opinion about.
    /// </para>
    /// </summary>
    public static bool IsKnown(string? code)
    {
        if (!HasValidShape(code))
            return false;

        return Known.Count == 0 || Known.Contains(code!.ToUpperInvariant());
    }

    /// <summary>Upper-cased code, or null when the input is blank. Shape is NOT checked here.</summary>
    public static string? Normalize(string? code)
        => string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
}

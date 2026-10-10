namespace Sankore.Modules.Leads.Domain;

using System.Text.RegularExpressions;

/// <summary>
/// The two tenant-supplied styling values that reach a third-party page.
///
/// <para>
/// <c>HostedFormConfig.AccentColor</c> and <c>FontFamily</c> are interpolated by the SDK into a
/// <c>&lt;style&gt;</c> element inside the hosted form's shadow root. A value carrying <c>}</c>
/// closes the rule it sits in, so everything after it becomes CSS of the tenant's choosing on a
/// page the tenant does not own. Shadow DOM contains selectors, not a broken-out rule's reach
/// over inherited properties, and nothing else in the pipeline escapes these — they are copied
/// from settings to a JSON response to a stylesheet as-is.
/// </para>
///
/// <para>
/// Allow-listed rather than escaped: the set of legal values here is small and closed, and an
/// escaping rule would have to agree with the SDK's interpolation forever. Anything outside the
/// list is dropped, which falls back to the SDK's own default — a form in the wrong colour, not a
/// form that styles its host.
/// </para>
/// </summary>
public static partial class CssColor
{
    /// <summary><c>#rgb</c>, <c>#rrggbb</c>, <c>#rrggbbaa</c>, or a bare CSS keyword.</summary>
    [GeneratedRegex(@"^(#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})|[a-zA-Z]{3,20})$")]
    private static partial Regex ColorPattern();

    /// <summary>
    /// A font stack: names, quoted names, and the commas and spaces between them. No url(),
    /// no parentheses, no semicolons.
    /// </summary>
    [GeneratedRegex("^[a-zA-Z0-9 ,'\"_-]{1,120}$")]
    private static partial Regex FontFamilyPattern();

    /// <summary>True when the value is safe to interpolate into a declaration.</summary>
    public static bool IsValidColor(string? value)
        => !string.IsNullOrWhiteSpace(value) && ColorPattern().IsMatch(value.Trim());

    public static bool IsValidFontFamily(string? value)
        => !string.IsNullOrWhiteSpace(value) && FontFamilyPattern().IsMatch(value.Trim());

    /// <summary>The trimmed value, or null when it is absent or not a colour.</summary>
    public static string? SanitizeOrNull(string? value)
        => IsValidColor(value) ? value!.Trim() : null;

    public static string? SanitizeFontFamilyOrNull(string? value)
        => IsValidFontFamily(value) ? value!.Trim() : null;
}

namespace Sankore.Modules.Administration.Infrastructure;

/// <summary>
/// Whether two typed numbers are the same line, for the test-mode recipient check only.
///
/// <para>
/// A user types « 07 08 09 18 01 » in their profile and a message leaves as « +2250708091801 »:
/// the platform deliberately infers no calling code (see <c>SensitiveValueNormalizer</c>), so the
/// two are compared on digits, the shorter being the END of the longer. At least eight digits must
/// agree — below that, a short extension would match half the country. This is an allow-list for a
/// sandbox, never an identity check: a near miss sends a test message to a colleague's handset.
/// </para>
/// </summary>
internal static class TestModePhones
{
    private const int MinimumDigits = 8;

    public static bool Same(string? a, string? b)
    {
        var x = Digits(a);
        var y = Digits(b);
        if (x.Length < MinimumDigits || y.Length < MinimumDigits) return false;

        return x.Length >= y.Length ? x.EndsWith(y, StringComparison.Ordinal) : y.EndsWith(x, StringComparison.Ordinal);
    }

    private static string Digits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        return digits.StartsWith("00", StringComparison.Ordinal) ? digits[2..] : digits;
    }
}

namespace Sankore.Modules.Customers.Domain.Matching;

/// <summary>
/// Default <see cref="IPhoneticKeyCalculator"/>: West-African orthographic folding followed by
/// a Double Metaphone primary code. Stateless and thread-safe; registered as a singleton.
/// </summary>
internal sealed class WestAfricanPhoneticKeyCalculator : IPhoneticKeyCalculator
{
    public string? Compute(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var normalized = WestAfricanNameNormalizer.Normalize(name);
        if (normalized.Length == 0)
            return null;

        var key = DoubleMetaphone.Encode(normalized);
        return key.Length == 0 ? null : key;
    }
}

namespace Sankore.Modules.Customers.Domain.Matching;

/// <summary>
/// Turns a name into a phonetic key used for duplicate detection.
/// The key is stored on the client (<c>PhoneticKeyPrimary</c> / <c>PhoneticKeySecondary</c>)
/// so that duplicate searches are plain indexed equality queries, never a scan.
/// </summary>
public interface IPhoneticKeyCalculator
{
    /// <summary>Returns the phonetic key of <paramref name="name"/>, or null when it is null/blank.</summary>
    string? Compute(string? name);
}

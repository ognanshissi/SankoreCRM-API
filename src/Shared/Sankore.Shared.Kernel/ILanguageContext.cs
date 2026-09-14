namespace Sankore.Shared.Kernel;

/// <summary>
/// Resolves the active language for the current request/operation.
/// Implemented in Sankore.Shared.Infrastructure by reading the JWT "lang" claim
/// or the Accept-Language header. Every feature that produces localised content
/// depends on this abstraction (never on HttpContext directly) so it stays
/// framework-agnostic and unit-testable.
/// </summary>
public interface ILanguageContext
{
    Languages CurrentLanguage { get; }
}

/// <summary>
/// Fixed implementation for use in tests and background jobs where there is
/// no HTTP request to read a claim from.
/// </summary>
public sealed class FixedLanguageContext(Languages language = Languages.Fr) : ILanguageContext
{
    public Languages CurrentLanguage { get; } = language;
}

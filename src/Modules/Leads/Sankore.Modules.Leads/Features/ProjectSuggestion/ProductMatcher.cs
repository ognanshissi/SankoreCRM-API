namespace Sankore.Modules.Leads.Features.ProjectSuggestion;

using System.Text;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>What the product was recognised by, shown to the agent as the reason of the proposal.</summary>
public enum ProductMatchBasis { Code, Name, SimilarName, Description }

public sealed record ProductMatch(ProductSummary Product, int Confidence, ProductMatchBasis Basis);

/// <summary>
/// Which catalogue products the prospect's « produit souhaité » looks like (2026-10-31, product
/// owner): its code, its name — in another order, with a typo, or only part of it — or the words of
/// its description. A SUGGESTION only, which a person confirms by creating the project: the line a
/// project carries is still decided by <c>IAdministrationModule.ResolveProductLineAsync</c>, which is
/// exact on purpose (it drives the KYC tier and the conversion gates) and is not touched here.
///
/// <para>
/// Pure: no database, no clock. Accents, case and punctuation are folded, and the small words of a
/// French label (« de », « pour »…) do not count. A confidence below <see cref="MinConfidence"/> is
/// not proposed — a loose match the agent has to correct is worse than none.
/// </para>
/// </summary>
internal static class ProductMatcher
{
    public const int MinConfidence = 60;

    private static readonly HashSet<string> StopWords =
        ["DE", "DU", "DES", "LA", "LE", "LES", "L", "D", "ET", "OU", "POUR", "UN", "UNE", "AU", "AUX", "EN", "A", "MON", "MA", "MES"];

    /// <summary>The active products the label looks like, best first, at most <paramref name="take"/>.</summary>
    public static IReadOnlyList<ProductMatch> Rank(string? interest, IEnumerable<ProductSummary> products, int take = 3)
    {
        var wanted = Normalize(interest);
        if (wanted.Length == 0 || wanted == "UNKNOWN") return [];
        var wantedTokens = Tokens(wanted);

        return products
            .Where(p => p.IsActive)
            .Select(p => Score(wanted, wantedTokens, p))
            .Where(m => m is not null && m.Confidence >= MinConfidence)
            .Select(m => m!)
            .OrderByDescending(m => m.Confidence)
            .ThenBy(m => m.Product.Name, StringComparer.Ordinal)
            .Take(take)
            .ToList();
    }

    private static ProductMatch? Score(string wanted, string[] wantedTokens, ProductSummary product)
    {
        var code = Normalize(product.Code);
        var name = Normalize(product.Name);
        var nameTokens = Tokens(name);

        // The code, typed as is or inside a sentence (« le CC01 svp »), separators ignored.
        if (code.Length > 0 && Compact(wanted) == Compact(code))
            return new(product, 100, ProductMatchBasis.Code);
        if (code.Length >= 3 && wantedTokens.Contains(Compact(code)))
            return new(product, 95, ProductMatchBasis.Code);

        if (name.Length > 0 && wanted == name)
            return new(product, 100, ProductMatchBasis.Name);

        var best = 0;
        if (nameTokens.Length > 0 && wantedTokens.Length > 0)
        {
            var common = nameTokens.Count(t => wantedTokens.Contains(t));
            if (common == nameTokens.Length && common == wantedTokens.Length) best = 97;      // same words, another order
            else if (common == nameTokens.Length) best = 90;                                 // the whole name, and more
            else if (common == wantedTokens.Length) best = 70 + 25 * common / nameTokens.Length; // part of the name

            // A typo: every word of the name found, near enough, among the words typed.
            var near = nameTokens.Count(t => wantedTokens.Any(w => Near(w, t)));
            if (near == nameTokens.Length) best = Math.Max(best, 85);
        }
        if (name.Length > 0)
        {
            var similarity = IdentityMatchScorer.JaroWinkler(wanted, name);
            if (similarity >= 0.9) best = Math.Max(best, (int)Math.Round(similarity * 90));
        }
        if (best >= MinConfidence)
            return new(product, best, best >= 97 ? ProductMatchBasis.Name : ProductMatchBasis.SimilarName);

        // The words of the description: most of what was typed is there.
        var described = Tokens(Normalize(product.Description)).Where(t => t.Length >= 3).ToArray();
        var meaningful = wantedTokens.Where(t => t.Length >= 3).ToArray();
        if (described.Length > 0 && meaningful.Length > 0)
        {
            var found = meaningful.Count(w => described.Any(d => d == w || Near(w, d)));
            var share = found / (double)meaningful.Length;
            if (share >= 0.6) return new(product, (int)Math.Round(50 + 20 * share), ProductMatchBasis.Description);
        }
        return null;
    }

    private static bool Near(string a, string b)
        => a.Length >= 4 && b.Length >= 4 && IdentityMatchScorer.JaroWinkler(a, b) >= 0.9;

    /// <summary>Upper-case, accent-free, every run of punctuation or space one space.</summary>
    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var folded = SensitiveValueNormalizer.RemoveDiacritics(value).ToUpperInvariant();
        var sb = new StringBuilder(folded.Length);
        var space = false;
        foreach (var c in folded)
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(c); space = false; }
            else if (!space && sb.Length > 0) { sb.Append(' '); space = true; }
        }
        return sb.ToString().TrimEnd();
    }

    private static string[] Tokens(string normalized)
        => normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !StopWords.Contains(t)).Distinct().ToArray();

    private static string Compact(string normalized) => normalized.Replace(" ", "");
}

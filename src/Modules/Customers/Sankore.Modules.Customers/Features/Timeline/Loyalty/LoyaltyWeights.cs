namespace Sankore.Modules.Customers.Features.Timeline.Loyalty;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Weights of the loyalty-score components, from the tenant setting <c>loyalty-weights-json</c>
/// (US-M01-BE-28). Default: <c>{"tenure":30,"regularity":30,"volume":20,"products":20}</c>.
///
/// ── Why two components are pinned to zero ────────────────────────────────────
/// <see cref="Volume"/> (transaction volume) and <see cref="Products"/> (number of products
/// held) belong to M03 (Savings) and M04 (Credit). Neither module exposes a contract yet, so
/// their component SCORE is 0 and reported as unavailable in the breakdown.
///
/// Crucially, their WEIGHT is excluded from the denominator instead of scoring zero out of 20:
/// with the default weights a perfectly loyal client would otherwise be capped at 60/100 and
/// every client would look mediocre. So the score is normalized over the AVAILABLE weights
/// only (tenure + regularity), and <c>BreakdownJson</c> records which components were
/// unavailable — the day M03/M04 land, their weight simply re-enters the denominator and no
/// caller changes.
/// </summary>
public sealed record LoyaltyWeights(
    [property: JsonPropertyName("tenure")] int Tenure,
    [property: JsonPropertyName("regularity")] int Regularity,
    [property: JsonPropertyName("volume")] int Volume,
    [property: JsonPropertyName("products")] int Products)
{
    public static LoyaltyWeights Default { get; } = new(30, 30, 20, 20);

    /// <summary>Weights of the components that can actually be computed today.</summary>
    public int AvailableWeightTotal => Math.Max(0, Tenure) + Math.Max(0, Regularity);

    /// <summary>
    /// Parses the setting, falling back to <see cref="Default"/> on malformed or empty JSON:
    /// a broken setting must degrade the score, not abort the nightly run.
    /// </summary>
    public static LoyaltyWeights Parse(string? json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json)) return Default;

        try
        {
            var parsed = JsonSerializer.Deserialize<LoyaltyWeights>(json);
            if (parsed is null) return Default;

            // A rule set of all zeros would make every score 0 with no way to tell that from
            // "nobody is loyal" — treat it as unset.
            return parsed.AvailableWeightTotal <= 0 ? Default : parsed;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return Default;
        }
    }
}

/// <summary>
/// One line of the score breakdown persisted in <c>ClientLoyaltyScore.BreakdownJson</c>.
/// </summary>
/// <param name="Component">Component name: <c>tenure</c>, <c>regularity</c>, <c>volume</c>, <c>products</c>.</param>
/// <param name="Weight">Configured weight.</param>
/// <param name="RawValue">Component value before weighting, 0..1. Null when unavailable.</param>
/// <param name="Points">Contribution to the final score, after normalization over available weights.</param>
/// <param name="IsAvailable">False when the owning module (M03/M04) is not wired yet.</param>
/// <param name="UnavailableReason">Machine-readable reason, e.g. <c>MODULE_NOT_AVAILABLE</c>.</param>
public sealed record LoyaltyComponentBreakdown(
    string Component,
    int Weight,
    double? RawValue,
    double Points,
    bool IsAvailable,
    string? UnavailableReason);

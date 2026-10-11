namespace Sankore.Shared.Kernel.Compliance;

/// <summary>
/// Answers the two questions the CIMA professional-card rule needs: whether a product line is an
/// insurance line, and whether a given user may present insurance operations on a given date.
///
/// <para>
/// Implemented by the Administration module — the only module that owns both the product
/// catalogue and the register of cards — and consumed through this Kernel contract so that
/// <c>InsuranceDistributionGuard</c>, which lives in shared infrastructure, depends on no
/// module's assembly. The same arrangement <see cref="Authorization.IAgencyScopeProvider"/> uses
/// for the agency tree, and for the same reason.
/// </para>
/// </summary>
public interface IDistributionComplianceProvider
{
    /// <summary>
    /// Whether <paramref name="productLineId"/> is one of the three insurance categories
    /// (<c>ProductCategories.Insurance</c>) and therefore requires a card of its presenter.
    ///
    /// <para>
    /// <c>false</c> for a line that cannot be resolved. An opaque reference whose product
    /// somebody has since removed must not make a lead unworkable — the convention
    /// <c>Lead.ProductLineId</c> already follows everywhere else.
    /// </para>
    /// </summary>
    Task<bool> IsInsuranceProductLineAsync(Guid tenantId, Guid productLineId, CancellationToken ct);

    /// <summary>
    /// Whether <paramref name="userId"/> held a valid professional card on
    /// <paramref name="asOf"/>, and why not when they did not.
    ///
    /// <para>
    /// <b>Never returns null</b>: a user with no recorded card gets
    /// <see cref="DistributionEligibility.Missing"/>, which is an answer. The card NUMBER is
    /// never returned — it identifies a named person on a regulator's register.
    /// </para>
    ///
    /// <para>
    /// <paramref name="asOf"/> is explicit because the question is historical as often as it is
    /// current: a controller asks whether the agent was licensed on the day they presented a
    /// contract.
    /// </para>
    /// </summary>
    Task<DistributionEligibilitySnapshot> GetDistributionEligibilityAsync(
        Guid tenantId, Guid userId, DateOnly asOf, CancellationToken ct);

    /// <summary>
    /// The same answer for a whole dispatching pool. Every id asked about is PRESENT in the
    /// result — a caller filtering a pool must be able to tell "not licensed" from "I did not ask
    /// about them".
    /// </summary>
    Task<IReadOnlyDictionary<Guid, DistributionEligibilitySnapshot>>
        GetDistributionEligibilitiesAsync(
            Guid tenantId, IReadOnlyCollection<Guid> userIds, DateOnly asOf, CancellationToken ct);
}

/// <summary>
/// Why a user may or may not present insurance operations. Named rather than a boolean, because
/// the four answers call for four different actions from whoever reads them.
/// </summary>
public enum DistributionEligibility
{
    /// <summary>A current card whose term covers the date asked about.</summary>
    Valid,

    /// <summary>
    /// No card has ever been recorded for this user. The ordinary state of an institution that
    /// has not started keeping the register.
    /// </summary>
    Missing,

    /// <summary>
    /// A card exists and its term does not cover the date asked about — or its recorded
    /// professional indemnity cover has lapsed, which invalidates it just as surely.
    /// </summary>
    Expired,

    /// <summary>
    /// A card whose row was closed — withdrawn, or suspended by the authority — and not replaced.
    /// Distinct from <see cref="Expired"/>: a lapse is a renewal nobody filed, a withdrawal is a
    /// decision somebody took, and only one of the two is cleared by paperwork.
    /// </summary>
    Withdrawn,
}

/// <param name="ExpiresOn">When the card stops being valid, or null when none is recorded.</param>
/// <param name="HasIndemnityCover">
/// Whether professional indemnity cover is recorded at all. A recorded cover that has LAPSED
/// makes the card invalid; an absent one does not.
/// </param>
/// <param name="TrainingHours">
/// Hours of insurance training recorded, for the micro-insurance requirement of CIMA Book VII
/// article 732. Null when none is recorded.
/// </param>
public sealed record DistributionEligibilitySnapshot(
    DistributionEligibility Status,
    DateOnly? ExpiresOn,
    bool HasIndemnityCover,
    int? TrainingHours)
{
    public bool IsValid => Status == DistributionEligibility.Valid;

    /// <summary>
    /// The answer for a user nobody has recorded a card for. A shared static is safe here: unlike
    /// an EF owned type's "empty" value, this record is never tracked.
    /// </summary>
    public static readonly DistributionEligibilitySnapshot NotRecorded =
        new(DistributionEligibility.Missing, null, false, null);
}

namespace Sankore.Modules.Integration.Features.Insurance.HoldingSubscription;

using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Turns an M01 holding into the subscription payload the insurance adapters already take
/// (BNF-B-16).
///
/// <para>
/// A mapper and nothing else, in its own type so the consumer stays about the queueing and the
/// decisions below are reviewable on their own. There are three of them, and each one is a choice
/// about what an insurer is told.
/// </para>
/// </summary>
internal static class HoldingPolicyPayload
{
    internal static InsurancePolicyPayload From(HoldingForSync holding)
    {
        ArgumentNullException.ThrowIfNull(holding);

        return new InsurancePolicyPayload(
            CrmCustomerId: holding.ClientId,
            CrmProductId:  holding.ProductLineId,
            // The CODE as snapshotted at subscription, not the catalogue's code today: a product
            // renamed or recoded since must not change what a signed contract says it was.
            InsurerProductCode: holding.ProductCode,
            // The terms' own effective date when there is one, else today. An insurer needs a
            // date; defaulting is better than refusing a subscription an agent has already had
            // signed, and the holding records which it was.
            EffectiveDate: holding.EffectiveFrom ?? DateOnly.FromDateTime(DateTime.UtcNow),
            PremiumAmount: holding.Premium ?? 0m,
            Currency:      holding.Currency ?? string.Empty,
            Periodicity:   Periodicity(holding.Periodicity),
            Beneficiaries:
            [
                .. holding.Designations.Select(d => new InsuranceBeneficiary(
                    FullName:     d.FullName,
                    // The ROLE when the agent typed no relationship: an insurer reading
                    // "BENEFICIAIREDECES" learns more than one reading nothing, and the role code
                    // is the institution's own wording by construction.
                    Relationship: d.Relationship ?? d.RoleCode,
                    // A role where a share means nothing (a proxy, a co-holder) carries none, and
                    // the adapters' contract takes a decimal. Zero is the honest rendering of
                    // "no quote-part" — never 100, which would assert an allocation nobody made.
                    SharePercent: d.SharePercent ?? 0m,
                    DateOfBirth:  d.BirthDate))
            ],
            LinkedLoanReference: null,
            // The signature reference IS the consent evidence for a subscription: it is what the
            // client signed, stored where the institution keeps it. Passing it is how ASS-12's
            // evidence requirement is satisfied without this module holding a second copy.
            ConsentEvidenceRef: holding.SignatureReference,
            CrmHoldingId:  holding.HoldingId,
            ClauseVersion: holding.ClauseVersion);
    }

    /// <summary>
    /// M01 stores the periodicity as a string an agent chose; the adapters take an enum. An
    /// unrecognised value falls back to <c>Single</c> rather than refusing: a premium paid once is
    /// the least wrong assumption, and the amount travels either way.
    /// </summary>
    private static PremiumPeriodicity Periodicity(string? raw)
        => Enum.TryParse<PremiumPeriodicity>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : PremiumPeriodicity.Single;
}

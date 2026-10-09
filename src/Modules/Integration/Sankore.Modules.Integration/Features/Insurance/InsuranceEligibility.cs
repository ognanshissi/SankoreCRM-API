namespace Sankore.Modules.Integration.Features.Insurance;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// What a tenant's own eligibility rules need to know about an applicant (ASS-03, criterion 1;
/// ASS-04, criterion 3).
///
/// <para>
/// An explicit record and not a set of module calls, so the rule is a pure function of facts. The
/// GATHERING of those facts is the subscription slice's job — the date of birth from M01, the tier
/// from M02, the account and the loan from <c>cbs_customer_snapshot</c> — and keeping it out of
/// here is what makes every rule testable without a database, a CBS or four module doubles.
/// </para>
///
/// <para>
/// Every field is NULLABLE because every one of them can be genuinely unknown, and the evaluator
/// treats unknown as <b>not satisfied</b> rather than as satisfied. A customer whose date of birth
/// M01 does not hold is not "of eligible age": it is a customer whose age nobody can state, and
/// approving a life policy on that basis is the failure this nullability exists to prevent.
/// </para>
/// </summary>
internal sealed record ApplicantFacts(
    int? AgeInYears,
    KycLevel? KycLevel,
    bool? HasCbsAccount,
    bool? HasActiveLoan);

/// <summary>
/// The product's eligibility rules, applied (ASS-03, criterion 1 — « âge, niveau KYC, détention
/// d'un compte ou d'un crédit »).
///
/// <para>
/// Pure and static: no database, no clock, no module. ASS-04's handler calls it after gathering
/// the facts, and it answers a <see cref="PublicApi.InsuranceEligibility"/> — the SAME record
/// <c>IInsuranceProductPort.CheckEligibilityAsync</c> returns, so a tenant rule and an insurer
/// rule are indistinguishable to the caller and their reasons concatenate into one explanation.
/// </para>
///
/// <para>
/// Reasons are codes, and a refusal carries <b>all</b> of them. ASS-04's third criterion says « un
/// refus est expliqué à l'agent »: one reason at a time turns a counter conversation into four,
/// and "not eligible" alone sends a customer away without knowing what to fix.
/// </para>
/// </summary>
internal static class InsuranceEligibilityRules
{
    internal const string TooYoung = "AGE_BELOW_MINIMUM";
    internal const string TooOld = "AGE_ABOVE_MAXIMUM";
    internal const string AgeUnknown = "AGE_UNKNOWN";
    internal const string KycInsufficient = "KYC_LEVEL_INSUFFICIENT";
    internal const string KycUnknown = "KYC_LEVEL_UNKNOWN";
    internal const string NoCbsAccount = "NO_CBS_ACCOUNT";
    internal const string CbsAccountUnknown = "CBS_ACCOUNT_UNKNOWN";
    internal const string NoActiveLoan = "NO_ACTIVE_LOAN";
    internal const string ActiveLoanUnknown = "ACTIVE_LOAN_UNKNOWN";

    internal static InsuranceEligibility Evaluate(InsuranceProduct product, ApplicantFacts facts)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(facts);

        var reasons = new List<string>();

        // Age is only consulted when the product constrains it: a product with no age rule must
        // not refuse a customer whose date of birth is missing.
        if (product.MinAge is not null || product.MaxAge is not null)
        {
            if (facts.AgeInYears is not { } age)
            {
                reasons.Add(AgeUnknown);
            }
            else
            {
                if (product.MinAge is { } min && age < min) reasons.Add(TooYoung);
                if (product.MaxAge is { } max && age > max) reasons.Add(TooOld);
            }
        }

        if (product.MinKycLevel != KycLevel.None)
        {
            if (facts.KycLevel is not { } level) reasons.Add(KycUnknown);

            // The enum is ordered None < Simplified < Full, so a numeric comparison IS the tier
            // comparison. Named here rather than left implicit: a reordering of the enum would
            // silently invert this rule, and the test that pins the order is in
            // InsuranceEligibilityRulesTests for exactly that reason.
            else if (level < product.MinKycLevel) reasons.Add(KycInsufficient);
        }

        if (product.RequiresCbsAccount)
        {
            if (facts.HasCbsAccount is not { } hasAccount) reasons.Add(CbsAccountUnknown);
            else if (!hasAccount) reasons.Add(NoCbsAccount);
        }

        if (product.RequiresActiveLoan)
        {
            if (facts.HasActiveLoan is not { } hasLoan) reasons.Add(ActiveLoanUnknown);
            else if (!hasLoan) reasons.Add(NoActiveLoan);
        }

        return new InsuranceEligibility(reasons.Count == 0, reasons);
    }
}

namespace Sankore.Shared.Kernel;

/// <summary>
/// The family a catalogue product belongs to. Persisted as a STRING everywhere
/// (<c>HasConversion&lt;string&gt;</c>), so appending a value is safe and reordering is harmless.
///
/// <para>
/// BNF-B-06 lists <c>CurrentAccount, Savings, Credit, Insurance, Other</c>. Four of those already
/// had a home here — <c>Credit</c> under the name <see cref="Loan"/> — so only
/// <see cref="CurrentAccount"/> was added.
/// </para>
///
/// <para>
/// <b>There is deliberately no <c>Other</c>.</b> A category is what resolves a product's
/// beneficiary policy, its required KYC tier and its integration system; a product filed under
/// "Other" has none of those answerable, and the failure would surface as a conversion refused for
/// reasons nobody can act on. A product that fits no value here needs a new value, which is one
/// line and a migration-free deploy.
/// </para>
/// </summary>
public enum ProductCategory
{
    Loan,
    Savings,
    Insurance,
    HealthInsurance,
    ForecastInsurance,
    Tontine,
    GroupCredit,
    Agriculture,

    /// <summary>A sight/transaction account (BNF-B-06). Appended, so no stored value moves.</summary>
    CurrentAccount,
}
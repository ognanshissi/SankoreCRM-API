namespace Sankore.Modules.Integration.Features.Onboarding;

/// <summary>
/// Answers the one question criterion 2 of INT-14 turns on: has a product been chosen for this
/// customer, and if so which CRM product code should the account be opened on.
///
/// <para>
/// <b>Why this is an interface with a single implementation that always says "no".</b> No public
/// contract of this platform exposes a product choice attached to a customer.
/// <c>ICustomersModule</c> returns <c>ClientSummary</c> / <c>CustomerSummary</c> — neither
/// carries a product; <c>IKycModule</c> returns statuses, limits and flow usage;
/// <c>KycValidatedEvent</c> carries a tenant, a customer and a timestamp.
/// <c>IAdministrationModule</c> can translate a product code into a category
/// (<c>GetProductCategoryAsync</c>) but has no notion of a product a given customer subscribed
/// to. So the chain has nothing to read, and the only two alternatives — inventing a product
/// code, or defaulting to "the tenant's first savings product" — would both open a real account
/// in a real core banking system on a decision nobody made.
/// </para>
///
/// <para>
/// It is therefore a named decision point and not a feature: the chain asks, the answer is "no
/// product", and the onboarding stops after the KYC level. The day a module owns the product
/// chosen at enrolment (M03 Épargne, or a subscription slice of M01), it registers its own
/// implementation and the chain's second half starts working with no change to the consumer.
/// Keeping the question here rather than hard-coding the "no" also means the OpenAccount half of
/// criterion 2 is covered by a test instead of being unreachable code.
/// </para>
/// </summary>
internal interface IOnboardingProductSelector
{
    /// <summary>
    /// The CRM product code to open an account on, or <c>null</c> when no product was chosen.
    ///
    /// <para>
    /// A CRM code, never a CBS one: the adapter translates it through <c>integration_mapping</c>
    /// (INT-04), and an unknown code is a Technical rejection naming the domain rather than a
    /// pass-through the external system has never heard of.
    /// </para>
    /// </summary>
    Task<string?> SelectAsync(Guid tenantId, Guid crmCustomerId, CancellationToken ct);
}

/// <summary>
/// The answer this platform can honestly give today: no product was chosen.
///
/// <para>
/// Not a stub waiting to be filled in — it is the correct answer for every tenant as long as
/// nothing records a product choice at enrolment. It reads nothing and can therefore never be
/// the reason an account is opened.
/// </para>
/// </summary>
internal sealed class NoOnboardingProductSelector : IOnboardingProductSelector
{
    public Task<string?> SelectAsync(Guid tenantId, Guid crmCustomerId, CancellationToken ct)
        => Task.FromResult<string?>(null);
}

namespace Sankore.Modules.Integration.Domain;

using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// One insurance product a tenant distributes, at one insurer (ASS-03).
///
/// <para>
/// Per connection and not per tenant, for the same reason <see cref="IntegrationMapping"/> is:
/// the insurer IS the connection, and an IMF distributing for two insurers legitimately sells two
/// products that look alike and have different codes, prices, guarantees and commission rates.
/// <see cref="ConnectionId"/> is an intra-schema foreign key, so a configured product can never
/// name an insurer that does not exist.
/// </para>
///
/// <para>
/// <b>There is no "is offerable" column.</b> ASS-03's fourth criterion — a product whose
/// connection is inactive is not offerable — is a statement about two rows, and the second one
/// changes without this one being touched: an administrator deactivates the ORASS connection and
/// every product of that insurer must stop being offerable in the same instant. A flag here would
/// be correct until that moment and wrong afterwards, with nothing to recompute it.
/// <see cref="IsActive"/> is the tenant's own decision about THIS product; offerability is derived
/// from it, from the connection, from the validity window and from the adapter's capabilities, in
/// one place — <c>ProductOfferability</c>.
/// </para>
/// </summary>
public sealed class InsuranceProduct : AggregateRoot
{
    /// <summary>Longest a guarantee list may be, serialised. See <see cref="GuaranteesJson"/>.</summary>
    internal const int MaxGuaranteesJsonLength = 8000;

    public Guid Id { get; private set; }

    /// <summary>The insurer. Intra-schema FK to <c>integration_connection</c>.</summary>
    public Guid ConnectionId { get; private set; }

    /// <summary>
    /// The product's code AT THE INSURER — what travels in a subscription payload. The tenant's
    /// own label is <see cref="Name"/>; the two are not interchangeable and conflating them is how
    /// a subscription is sent with a code the insurer has never heard of.
    /// </summary>
    public string InsurerProductCode { get; private set; } = string.Empty;

    /// <summary>Operator- and agent-facing name. What the counter screen shows.</summary>
    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>
    /// Serialised <see cref="InsuranceGuarantee"/> list, held as <c>jsonb</c>.
    ///
    /// <para>
    /// jsonb and not a child table, the same call <see cref="CbsSnapshot"/> makes: nothing queries
    /// across the guarantees of different products, the list is read whole to render one product,
    /// and a child table would cost a delete-and-reinsert on every edit for joins nobody performs.
    /// Bounded at <see cref="MaxGuaranteesJsonLength"/> in the validator rather than in the column,
    /// because a <c>jsonb</c> length cap is not a thing PostgreSQL offers.
    /// </para>
    /// </summary>
    public string GuaranteesJson { get; private set; } = "[]";

    public PremiumPeriodicity Periodicity { get; private set; }

    public ProductPricingMode PricingMode { get; private set; }

    /// <summary>
    /// The premium, when <see cref="PricingMode"/> is
    /// <see cref="ProductPricingMode.CatalogueFixed"/>. Null for an insurer-priced product, and
    /// the invariant is enforced in <see cref="Create"/>: a fixed-price product without a price
    /// would be offered with no premium to debit.
    /// </summary>
    public decimal? FixedPremiumAmount { get; private set; }

    /// <summary>ISO 4217, three letters. Required whenever an amount is present.</summary>
    public string? Currency { get; private set; }

    /// <summary>The capital insured, when the product has a single standard one. Informational.</summary>
    public decimal? InsuredAmount { get; private set; }

    // ── Eligibility (ASS-03, criterion 1) ───────────────────────────────────

    /// <summary>Minimum age at the effective date, in whole years. Null means no floor.</summary>
    public int? MinAge { get; private set; }

    /// <summary>Maximum age at the effective date. Null means no ceiling.</summary>
    public int? MaxAge { get; private set; }

    /// <summary>
    /// The KYC tier the customer must hold. <see cref="KycLevel.None"/> means none is required —
    /// stored rather than left null so the three tiers read as a total order in the evaluator and
    /// a null never has to mean "no requirement" in one place and "unknown" in another.
    /// </summary>
    public KycLevel MinKycLevel { get; private set; }

    /// <summary>The customer must hold an account in the CBS.</summary>
    public bool RequiresCbsAccount { get; private set; }

    /// <summary>The customer must hold a live loan — the shape of a borrower's insurance.</summary>
    public bool RequiresActiveLoan { get; private set; }

    /// <summary>
    /// The credit product this insurance is attached to, as its code in M12's catalogue
    /// (ASS-03, criterion 3 — <i>assurance emprunteur</i>).
    ///
    /// <para>
    /// A <b>string code and no foreign key</b>: M12's products live in the <c>administration</c>
    /// schema and this module never puts a physical key across a schema boundary. The code is
    /// checked against <c>IAdministrationModule.GetProductCategoryAsync</c> when the row is
    /// written — it must resolve, and resolve to <c>Loan</c> — and every reader afterwards
    /// degrades to "unlinked" rather than assuming it still resolves, exactly as
    /// <c>LeadAssignment.RuleId</c> and <c>IntegrationConnection.RelayAgentId</c> do.
    /// </para>
    /// </summary>
    public string? LinkedCreditProductCode { get; private set; }

    // ── Commercial terms ────────────────────────────────────────────────────

    /// <summary>
    /// The IMF's commission on this product, as a rate in [0, 1] (ASS-10, criterion 3).
    ///
    /// <para>
    /// On the product because that is where ASS-10 says the rate is defined. A statement line
    /// keeps its own copy of the rate it applied, so changing this never re-writes a month that
    /// has already been justified.
    /// </para>
    /// </summary>
    public decimal CommissionRate { get; private set; }

    /// <summary>
    /// How many times a failed premium instalment is retried before the <i>impayé</i> is declared
    /// to the insurer (ASS-08, criterion 2 — « une politique paramétrable par produit »).
    /// </summary>
    public int PremiumRetryLimit { get; private set; }

    /// <summary>Days between two retries of a failed instalment.</summary>
    public int PremiumRetryIntervalDays { get; private set; }

    // ── Lifecycle ───────────────────────────────────────────────────────────

    /// <summary>The tenant's own switch. Not the same thing as offerable — see the class remarks.</summary>
    public bool IsActive { get; private set; }

    /// <summary>First day the product may be subscribed. Inclusive.</summary>
    public DateOnly EffectiveFrom { get; private set; }

    /// <summary>Last day it may be subscribed, inclusive. Null means open-ended.</summary>
    public DateOnly? EffectiveTo { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    /// <summary>PostgreSQL xmin. Five endpoints mutate this row.</summary>
    public uint Version { get; private set; }

    private InsuranceProduct() { }

    public static InsuranceProduct Create(
        Guid tenantId,
        Guid connectionId,
        string insurerProductCode,
        string name,
        PremiumPeriodicity periodicity,
        ProductPricingMode pricingMode,
        Guid createdBy,
        TimeProvider clock,
        DateOnly effectiveFrom,
        string? description = null,
        string guaranteesJson = "[]",
        decimal? fixedPremiumAmount = null,
        string? currency = null,
        decimal? insuredAmount = null,
        int? minAge = null,
        int? maxAge = null,
        KycLevel minKycLevel = KycLevel.None,
        bool requiresCbsAccount = false,
        bool requiresActiveLoan = false,
        string? linkedCreditProductCode = null,
        decimal commissionRate = 0m,
        int premiumRetryLimit = 3,
        int premiumRetryIntervalDays = 3,
        DateOnly? effectiveTo = null,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (connectionId == Guid.Empty) throw new DomainException("ConnectionId is required.");
        if (string.IsNullOrWhiteSpace(insurerProductCode))
            throw new DomainException("An insurer product code is required.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("A product name is required.");
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.GetUtcNow();

        var product = new InsuranceProduct
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            // Trimmed, not case-folded: the insurer's codes are its own.
            InsurerProductCode = insurerProductCode.Trim(),
            Name = name.Trim(),
            Description = description?.Trim(),
            GuaranteesJson = string.IsNullOrWhiteSpace(guaranteesJson) ? "[]" : guaranteesJson,
            Periodicity = periodicity,
            CreatedAt = now,
            CreatedBy = createdBy,
            UpdatedAt = now,
            // Created inactive, like a connection: a product is configured in several steps and
            // must not be offerable between the first save and the last.
            IsActive = false,
            EffectiveFrom = effectiveFrom,
        };

        product.ApplyTerms(
            pricingMode, fixedPremiumAmount, currency, insuredAmount,
            minAge, maxAge, minKycLevel, requiresCbsAccount, requiresActiveLoan,
            linkedCreditProductCode, commissionRate, premiumRetryLimit, premiumRetryIntervalDays,
            effectiveTo);

        return product;
    }

    /// <summary>
    /// Edits everything an administrator may change. The insurer (<see cref="ConnectionId"/>) and
    /// the insurer's code are NOT among them: changing either would silently re-point every
    /// subscription and policy that already names this product at a different contract at a
    /// different insurer. Re-pointing is a new product, which is why it is not an edit.
    /// </summary>
    public void Update(
        string name,
        string? description,
        string guaranteesJson,
        PremiumPeriodicity periodicity,
        ProductPricingMode pricingMode,
        decimal? fixedPremiumAmount,
        string? currency,
        decimal? insuredAmount,
        int? minAge,
        int? maxAge,
        KycLevel minKycLevel,
        bool requiresCbsAccount,
        bool requiresActiveLoan,
        string? linkedCreditProductCode,
        decimal commissionRate,
        int premiumRetryLimit,
        int premiumRetryIntervalDays,
        DateOnly effectiveFrom,
        DateOnly? effectiveTo,
        Guid updatedBy,
        TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("A product name is required.");
        ArgumentNullException.ThrowIfNull(clock);

        Name = name.Trim();
        Description = description?.Trim();
        GuaranteesJson = string.IsNullOrWhiteSpace(guaranteesJson) ? "[]" : guaranteesJson;
        Periodicity = periodicity;
        EffectiveFrom = effectiveFrom;

        ApplyTerms(
            pricingMode, fixedPremiumAmount, currency, insuredAmount,
            minAge, maxAge, minKycLevel, requiresCbsAccount, requiresActiveLoan,
            linkedCreditProductCode, commissionRate, premiumRetryLimit, premiumRetryIntervalDays,
            effectiveTo);

        UpdatedBy = updatedBy;
        UpdatedAt = clock.GetUtcNow();
    }

    public void Activate(Guid actor, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        IsActive = true;
        UpdatedBy = actor;
        UpdatedAt = clock.GetUtcNow();
    }

    /// <summary>
    /// Withdrawn from the catalogue, never deleted: subscriptions, policies, instalments and a
    /// year of statement lines name this product, and a deleted row would make them unreadable.
    /// Existing policies keep running — a product withdrawal stops new subscriptions, it does not
    /// cancel contracts.
    /// </summary>
    public void Deactivate(Guid actor, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        IsActive = false;
        UpdatedBy = actor;
        UpdatedAt = clock.GetUtcNow();
    }

    /// <summary>
    /// The invariants shared by creation and edition, in one place so the two cannot drift.
    ///
    /// <para>
    /// All four are refusals a validator also makes, and they are repeated here on purpose: the
    /// validator protects the HTTP caller, this protects every other writer — a seeder, a future
    /// import, a consumer — and a product with a pricing mode its columns contradict is the kind
    /// of row that only shows itself at the counter.
    /// </para>
    /// </summary>
    private void ApplyTerms(
        ProductPricingMode pricingMode,
        decimal? fixedPremiumAmount,
        string? currency,
        decimal? insuredAmount,
        int? minAge,
        int? maxAge,
        KycLevel minKycLevel,
        bool requiresCbsAccount,
        bool requiresActiveLoan,
        string? linkedCreditProductCode,
        decimal commissionRate,
        int premiumRetryLimit,
        int premiumRetryIntervalDays,
        DateOnly? effectiveTo)
    {
        if (pricingMode == ProductPricingMode.CatalogueFixed)
        {
            if (fixedPremiumAmount is null or <= 0m)
                throw new DomainException(
                    "A catalogue-priced product needs a positive premium: it is the amount that "
                    + "will be debited from the customer's account.");

            if (string.IsNullOrWhiteSpace(currency))
                throw new DomainException("A premium amount requires its currency.");
        }
        else
        {
            // Not merely ignored — blanked. A stale amount left behind an insurer-priced product
            // is the value a screen would show, and a debit would use, the day the insurer call
            // fails.
            fixedPremiumAmount = null;
        }

        if (minAge is not null && maxAge is not null && minAge > maxAge)
            throw new DomainException("The minimum age cannot exceed the maximum age.");

        if (commissionRate is < 0m or > 1m)
            throw new DomainException("A commission rate is a fraction between 0 and 1.");

        if (premiumRetryLimit < 0)
            throw new DomainException("A retry limit cannot be negative.");

        if (premiumRetryIntervalDays < 1)
            throw new DomainException(
                "A retry interval of less than a day would re-debit a refused account the same "
                + "day, which banks read as harassment of the account.");

        if (effectiveTo is not null && effectiveTo < EffectiveFrom)
            throw new DomainException("The product's validity window ends before it starts.");

        PricingMode = pricingMode;
        FixedPremiumAmount = fixedPremiumAmount;
        Currency = string.IsNullOrWhiteSpace(currency) ? null : currency.Trim().ToUpperInvariant();
        InsuredAmount = insuredAmount;
        MinAge = minAge;
        MaxAge = maxAge;
        MinKycLevel = minKycLevel;
        RequiresCbsAccount = requiresCbsAccount;
        RequiresActiveLoan = requiresActiveLoan;
        LinkedCreditProductCode = string.IsNullOrWhiteSpace(linkedCreditProductCode)
            ? null
            : linkedCreditProductCode.Trim();
        CommissionRate = commissionRate;
        PremiumRetryLimit = premiumRetryLimit;
        PremiumRetryIntervalDays = premiumRetryIntervalDays;
        EffectiveTo = effectiveTo;
    }
}

/// <summary>
/// One guarantee (<i>garantie</i>) of a product, as stored in <c>ins_product.guarantees</c>.
///
/// <para>
/// A record and not an entity: it has no identity of its own outside the product and nothing
/// queries one.
/// </para>
/// </summary>
public sealed record InsuranceGuarantee(
    string Code,
    string Label,
    decimal? CeilingAmount,
    decimal? Deductible);

namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>How a discount is expressed: a share of the amount, or an amount.</summary>
public enum QuoteDiscountKind { None, Percent, Amount }

/// <summary>Where a project's offer stands with respect to the institution's discount ceiling.</summary>
public enum QuoteApprovalStatus
{
    /// <summary>Within the ceiling, or no ceiling set: the offer can be issued.</summary>
    NotRequired,
    /// <summary>Beyond the ceiling: « À valider » — no quote can be generated or sent.</summary>
    Pending,
    /// <summary>A manager accepted these lines as they stand.</summary>
    Approved,
    /// <summary>A manager refused: the lines must change, which asks again.</summary>
    Rejected,
}

/// <summary>
/// The priced offer of a project (quote lines, lot 3, 2026-11-10, product owner): several Standard
/// products with quantity, price, discount and VAT, an optional global discount, and the totals.
/// One per project, created with its first line. Its own aggregate so every other read of a project
/// is untouched; the project carries the outcome — its estimated amount is <see cref="TotalExclTax"/>,
/// its product the first line's.
/// </summary>
/// <remarks>
/// <b>A line keeps the price and the rate of its entry</b>: a later change of the catalogue or of the
/// institution's rates leaves it alone. Replacing the lines keeps the frozen rate of a line sent back
/// with its id and the same product; a new line, or a line whose product changed, takes today's.
/// <para>
/// <b>The discount is measured against the catalogue</b>: lowering a line's price below the catalogue
/// is a discount as much as a percentage typed in the discount column, so the share compared with
/// the ceiling is 1 − total HT ÷ Σ (quantity × catalogue price, else the price entered).
/// </para>
/// </remarks>
public sealed class ProjectQuote
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OpportunityId { get; private set; }
    public string Currency { get; private set; } = InstitutionCurrencies.Default;

    public List<ProjectQuoteLine> Lines { get; private set; } = [];

    public QuoteDiscountKind GlobalDiscountKind { get; private set; }
    public decimal GlobalDiscountValue { get; private set; }

    // Totals, computed by QuoteTotals at each change and stored: lists and reports read them.
    public decimal SubtotalExclTax { get; private set; }
    public decimal GlobalDiscountAmount { get; private set; }
    public decimal TotalExclTax { get; private set; }
    public decimal TotalVat { get; private set; }
    public decimal TotalInclTax { get; private set; }
    /// <summary>Discount against the catalogue, in percent, two decimals.</summary>
    public decimal EffectiveDiscountPercent { get; private set; }
    /// <summary>The institution's ceiling when the lines were last saved; null = none.</summary>
    public decimal? DiscountCeilingPercent { get; private set; }

    public QuoteApprovalStatus ApprovalStatus { get; private set; }
    public Guid? DecidedBy { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public string? DecisionComment { get; private set; }

    public Guid? UpdatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    // ── Issued quotes (lot 5) ──────────────────────────────────────────────────
    /// <summary>The institution's number, taken at the first PDF (DEV-2026-000001); kept by every version.</summary>
    public string? Number { get; private set; }
    /// <summary>The last version issued; 0 = never.</summary>
    public int IssuedVersion { get; private set; }
    public DateTimeOffset? LastIssuedAt { get; private set; }

    /// <summary>The lines changed after the last PDF: that PDF no longer says what the offer is.</summary>
    public bool ChangedSinceIssue => LastIssuedAt is { } issued && UpdatedAt > issued;

    /// <summary>
    /// Records a new PDF of these lines: the first takes <paramref name="number"/>, each later one
    /// keeps it and adds a version. A PDF already issued is never touched — a new one is made.
    /// </summary>
    public Result<int> Issue(string number, DateTimeOffset now)
    {
        if (!CanBeIssued) return Result.Fail<int>(ApprovalStatus is QuoteApprovalStatus.Pending or QuoteApprovalStatus.Rejected
            ? "QUOTE_DISCOUNT_NOT_APPROVED" : "QUOTE_EMPTY");
        Number ??= number;
        IssuedVersion++;
        LastIssuedAt = now;
        return Result.Ok(IssuedVersion);
    }

    /// <summary>« DEV-2026-000001 », then « DEV-2026-000001 v2 ».</summary>
    public static string Label(string number, int version) => version <= 1 ? number : $"{number} v{version}";

    public const int MaxLines = 100;
    public const int MaxDecisionCommentLength = 500;

    private ProjectQuote() { }

    public static ProjectQuote Start(Guid tenantId, Guid opportunityId, string currency, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, OpportunityId = opportunityId,
        Currency = currency, CreatedAt = now, UpdatedAt = now,
    };

    /// <summary>
    /// Replaces the lines and the global discount, recomputes the totals and decides whether the
    /// offer needs a manager. Changing anything after an approval or a refusal asks again when the
    /// new discount is still beyond the ceiling.
    /// </summary>
    public Result Replace(
        IReadOnlyList<ProjectQuoteLine> lines, QuoteDiscountKind globalKind, decimal globalValue,
        decimal? ceilingPercent, Guid? actor, DateTimeOffset now)
    {
        if (lines.Count > MaxLines) return Result.Fail("QUOTE_TOO_MANY_LINES");
        if (!Enum.IsDefined(globalKind) || globalValue < 0 || (globalKind == QuoteDiscountKind.Percent && globalValue > 100))
            return Result.Fail("QUOTE_DISCOUNT_INVALID");
        if (globalKind == QuoteDiscountKind.None) globalValue = 0;

        var totals = QuoteTotals.Compute(lines, globalKind, globalValue, Currency);
        if (totals.IsFailure) return Result.Fail(totals.Error!);

        Lines = [.. lines];
        for (var i = 0; i < Lines.Count; i++) Lines[i].PlaceAt(i);
        GlobalDiscountKind = globalKind;
        GlobalDiscountValue = globalValue;
        Apply(totals.Value);

        DiscountCeilingPercent = ceilingPercent;
        ApprovalStatus = NeedsApproval(totals.Value.EffectiveDiscountPercent, ceilingPercent)
            ? QuoteApprovalStatus.Pending
            : QuoteApprovalStatus.NotRequired;
        DecidedBy = null;
        DecidedAt = null;
        DecisionComment = null;
        UpdatedBy = actor;
        UpdatedAt = now;
        return Result.Ok();
    }

    /// <summary>A manager's decision on a pending offer.</summary>
    public Result Decide(bool approve, Guid decidedBy, string? comment, DateTimeOffset now)
    {
        if (ApprovalStatus != QuoteApprovalStatus.Pending) return Result.Fail("QUOTE_NOT_PENDING_APPROVAL");
        var text = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (text is { Length: > MaxDecisionCommentLength }) return Result.Fail("QUOTE_DECISION_COMMENT_TOO_LONG");
        if (!approve && text is null) return Result.Fail("QUOTE_REJECTION_REASON_REQUIRED");

        ApprovalStatus = approve ? QuoteApprovalStatus.Approved : QuoteApprovalStatus.Rejected;
        DecidedBy = decidedBy;
        DecidedAt = now;
        DecisionComment = text;
        UpdatedAt = now;
        return Result.Ok();
    }

    /// <summary>Whether a quote may be generated or sent from these lines (lot 5).</summary>
    public bool CanBeIssued => Lines.Count > 0
        && ApprovalStatus is QuoteApprovalStatus.NotRequired or QuoteApprovalStatus.Approved;

    public static bool NeedsApproval(decimal effectiveDiscountPercent, decimal? ceilingPercent)
        => ceilingPercent is { } ceiling && effectiveDiscountPercent > ceiling;

    private void Apply(QuoteTotals t)
    {
        SubtotalExclTax = t.SubtotalExclTax;
        GlobalDiscountAmount = t.GlobalDiscountAmount;
        TotalExclTax = t.TotalExclTax;
        TotalVat = t.TotalVat;
        TotalInclTax = t.TotalInclTax;
        EffectiveDiscountPercent = t.EffectiveDiscountPercent;
    }
}

/// <summary>One product of a project's offer.</summary>
public sealed class ProjectQuoteLine
{
    public Guid Id { get; private set; }
    public int Position { get; private set; }
    /// <summary>The catalogue product (Standard, active when the line was entered).</summary>
    public Guid ProductId { get; private set; }
    public string ProductCode { get; private set; } = string.Empty;
    /// <summary>What the quote prints — the product's name by default, editable.</summary>
    public string Label { get; private set; } = string.Empty;
    public decimal Quantity { get; private set; }
    public string? Unit { get; private set; }
    /// <summary>Price excluding VAT the agent kept or typed.</summary>
    public decimal UnitPrice { get; private set; }
    /// <summary>The catalogue's price when the line was entered; null when the product had none.</summary>
    public decimal? CatalogueUnitPrice { get; private set; }
    public QuoteDiscountKind DiscountKind { get; private set; }
    public decimal DiscountValue { get; private set; }
    public ProductVatCategory VatCategory { get; private set; }
    /// <summary>The rate, in percent, frozen when the line was entered.</summary>
    public decimal VatRate { get; private set; }

    // Computed by QuoteTotals, stored for the PDF (lot 5).
    public decimal GrossExclTax { get; private set; }
    public decimal DiscountAmount { get; private set; }
    public decimal NetExclTax { get; private set; }
    public decimal VatAmount { get; private set; }
    public decimal TotalInclTax { get; private set; }

    public const int MaxLabelLength = 200;
    public const int MaxUnitLength = 30;
    public const decimal MaxQuantity = 1_000_000_000m;

    private ProjectQuoteLine() { }

    public static Result<ProjectQuoteLine> Create(
        Guid? id, Guid productId, string productCode, string label, decimal quantity, string? unit,
        decimal unitPrice, decimal? cataloguePrice, QuoteDiscountKind discountKind, decimal discountValue,
        ProductVatCategory vatCategory, decimal vatRate)
    {
        var text = label?.Trim() ?? string.Empty;
        if (text.Length == 0) return Result.Fail<ProjectQuoteLine>("QUOTE_LINE_LABEL_REQUIRED");
        if (text.Length > MaxLabelLength) return Result.Fail<ProjectQuoteLine>("QUOTE_LINE_LABEL_TOO_LONG");
        if (quantity <= 0 || quantity > MaxQuantity || decimal.Round(quantity, 3) != quantity)
            return Result.Fail<ProjectQuoteLine>("QUOTE_LINE_QUANTITY_INVALID");
        if (unitPrice < 0 || decimal.Round(unitPrice, 2) != unitPrice) return Result.Fail<ProjectQuoteLine>("QUOTE_LINE_PRICE_INVALID");
        var u = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
        if (u is { Length: > MaxUnitLength }) return Result.Fail<ProjectQuoteLine>("QUOTE_LINE_UNIT_TOO_LONG");
        if (!Enum.IsDefined(discountKind) || discountValue < 0
            || (discountKind == QuoteDiscountKind.Percent && discountValue > 100))
            return Result.Fail<ProjectQuoteLine>("QUOTE_DISCOUNT_INVALID");
        if (vatRate is < 0 or > 100) return Result.Fail<ProjectQuoteLine>("QUOTE_LINE_VAT_INVALID");

        return Result.Ok(new ProjectQuoteLine
        {
            Id = id is { } given && given != Guid.Empty ? given : Guid.NewGuid(),
            ProductId = productId,
            ProductCode = productCode,
            Label = text,
            Quantity = quantity,
            Unit = u,
            UnitPrice = unitPrice,
            CatalogueUnitPrice = cataloguePrice,
            DiscountKind = discountKind,
            DiscountValue = discountKind == QuoteDiscountKind.None ? 0 : discountValue,
            VatCategory = vatCategory,
            VatRate = vatRate,
        });
    }

    internal void PlaceAt(int position) => Position = position;

    internal void SetAmounts(decimal gross, decimal discount, decimal net, decimal vat, decimal total)
    {
        GrossExclTax = gross;
        DiscountAmount = discount;
        NetExclTax = net;
        VatAmount = vat;
        TotalInclTax = total;
    }
}

/// <summary>
/// The arithmetic of an offer, in one place. Rounded to the currency's minor units
/// (<see cref="InstitutionCurrencies.MinorDigits"/>), half away from zero.
/// <list type="number">
/// <item>Line: gross = quantity × price; discount = gross × % or the amount (refused above the gross);
/// net = gross − discount; VAT = net × rate; total = net + VAT.</item>
/// <item>Offer: subtotal = Σ net; the global discount (% or amount, not above the subtotal) comes
/// BEFORE the VAT (to confirm with an accountant, docs/devis-en-lignes.md § 6); total HT = subtotal − it.</item>
/// <item>VAT of the offer: per rate, on that rate's share of the total HT — so with a global
/// discount the base of each rate shrinks in proportion. Without one, the per-rate VAT can differ by
/// one unit from the sum of the rounded line VATs; the offer's figure is the one that counts.</item>
/// </list>
/// </summary>
public sealed record QuoteTotals(
    decimal SubtotalExclTax, decimal GlobalDiscountAmount, decimal TotalExclTax,
    decimal TotalVat, decimal TotalInclTax, decimal EffectiveDiscountPercent,
    IReadOnlyList<QuoteVatBreakdown> VatByRate)
{
    public static Result<QuoteTotals> Compute(
        IReadOnlyList<ProjectQuoteLine> lines, QuoteDiscountKind globalKind, decimal globalValue, string currency)
    {
        var digits = InstitutionCurrencies.MinorDigits(currency);
        decimal R(decimal v) => decimal.Round(v, digits, MidpointRounding.AwayFromZero);

        decimal subtotal = 0, reference = 0;
        foreach (var line in lines)
        {
            var gross = R(line.Quantity * line.UnitPrice);
            var discount = line.DiscountKind switch
            {
                QuoteDiscountKind.Percent => R(gross * line.DiscountValue / 100m),
                QuoteDiscountKind.Amount => line.DiscountValue,
                _ => 0m,
            };
            if (discount > gross) return Result.Fail<QuoteTotals>("QUOTE_DISCOUNT_ABOVE_AMOUNT");
            var net = gross - discount;
            var vat = R(net * line.VatRate / 100m);
            line.SetAmounts(gross, discount, net, vat, net + vat);
            subtotal += net;
            reference += R(line.Quantity * (line.CatalogueUnitPrice ?? line.UnitPrice));
        }

        var globalDiscount = globalKind switch
        {
            QuoteDiscountKind.Percent => R(subtotal * globalValue / 100m),
            QuoteDiscountKind.Amount => globalValue,
            _ => 0m,
        };
        if (globalDiscount > subtotal) return Result.Fail<QuoteTotals>("QUOTE_DISCOUNT_ABOVE_AMOUNT");
        var totalExcl = subtotal - globalDiscount;
        var factor = subtotal == 0 ? 0 : totalExcl / subtotal;

        var byRate = lines
            .GroupBy(l => l.VatRate)
            .OrderByDescending(g => g.Key)
            .Select(g =>
            {
                var baseAmount = R(g.Sum(l => l.NetExclTax) * factor);
                return new QuoteVatBreakdown(g.Key, baseAmount, R(baseAmount * g.Key / 100m));
            })
            .ToList();
        var totalVat = byRate.Sum(b => b.Vat);

        // Above the catalogue (a price raised) reads as no discount, never as a negative one.
        var effective = reference <= 0 ? 0 : Math.Max(0, decimal.Round((1 - totalExcl / reference) * 100m, 2, MidpointRounding.AwayFromZero));

        return Result.Ok(new QuoteTotals(subtotal, globalDiscount, totalExcl, totalVat, totalExcl + totalVat, effective, byRate));
    }
}

/// <param name="Rate">In percent.</param>
/// <param name="Base">Total HT at this rate, after the global discount.</param>
public sealed record QuoteVatBreakdown(decimal Rate, decimal Base, decimal Vat);

/// <summary>
/// One PDF generated from a project's offer (quote lines, lot 5): its number and version, its
/// dates and totals as printed, and the project file holding it — which is what the e-mail and
/// WhatsApp sending take. Never edited.
/// </summary>
public sealed class ProjectQuoteIssue : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OpportunityId { get; private set; }
    public Guid QuoteId { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public int Version { get; private set; }
    public DateTimeOffset IssuedAt { get; private set; }
    public Guid? IssuedBy { get; private set; }
    public DateOnly ValidUntil { get; private set; }
    public string Currency { get; private set; } = InstitutionCurrencies.Default;
    public decimal TotalExclTax { get; private set; }
    public decimal TotalInclTax { get; private set; }
    public Guid AttachmentId { get; private set; }

    private ProjectQuoteIssue() { }

    public static ProjectQuoteIssue Record(ProjectQuote quote, int version, DateOnly validUntil, Guid? by, Guid attachmentId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), TenantId = quote.TenantId, OpportunityId = quote.OpportunityId, QuoteId = quote.Id,
        Number = quote.Number!, Version = version, IssuedAt = now, IssuedBy = by, ValidUntil = validUntil,
        Currency = quote.Currency, TotalExclTax = quote.TotalExclTax, TotalInclTax = quote.TotalInclTax,
        AttachmentId = attachmentId,
    };
}

namespace Sankore.Modules.Administration.Domain;

using System.Text.RegularExpressions;
using Sankore.Shared.Kernel;

/// <summary>
/// The institution's compliance parameters: FATCA regime and PEP post-mandate window
/// (BNF-B-10, BNF-B-05). One row per tenant, created on first access with the platform defaults.
///
/// <para>
/// <b>Why M12 and not M13.</b> These are read by three modules — M13 for a lead's completeness,
/// M01 for the client record, M02 for the vigilance level. Putting them in a <c>leads_settings</c>
/// row would make M01 and M02 depend on M13 to learn whether their own institution is subject to
/// FATCA. They describe the INSTITUTION, and M12 is the module of the institution.
/// </para>
///
/// <para>
/// <b>Why a typed table and not a generic key/value store.</b> M12 has no such store — its only
/// precedent for a tenant parameter is <c>TenantNotificationSettings</c>, a dedicated table — and
/// these parameters have real invariants: a GIIN has a fixed 19-character structure, and enabling
/// FATCA without one is not a configuration, it is a half-configured regulatory obligation. A
/// <c>Value string</c> column pushes both rules into a parser that each caller has to remember to
/// run.
/// </para>
///
/// <para>
/// <b>PEP IS a switch, and it was not always.</b> This class used to say in as many words that
/// nothing turns PEP vigilance off — it follows the LBC/FT rules binding every UEMOA institution,
/// whereas FATCA depends on an intergovernmental agreement or on the institution's own IRS
/// registration. <see cref="PepEnabled"/> was added on the product owner's explicit decision,
/// against that reasoning and knowing it: the front end had shipped a per-tenant toggle and the
/// screen is the one the institution operates. The argument is recorded here rather than removed,
/// because whoever reads this next deserves to know the trade was made deliberately and not by
/// drift.
/// </para>
///
/// <para>
/// <b>What the switch does and does not do.</b> It governs COLLECTION: with PEP off, a lead is no
/// longer blocked for an undeclared exposure, and the front offers no PEP block. It does NOT erase
/// exposure already recorded, and it does NOT stop M01 or M02 recalculating exposure from data
/// already captured — a client whose spouse was declared politically exposed last year stays
/// flagged. Switching it off narrows what is asked of new files; it is not an undo.
/// </para>
/// </summary>
public sealed class ComplianceSettings : ITenant
{
    /// <summary>
    /// IRS GIIN structure: <c>XXXXXX.XXXXX.XX.XXX</c> — 6-character FATCA id, 5-character
    /// institution-type code, 2-letter category, 3-digit country identifier. 19 characters.
    ///
    /// <para>
    /// The SHAPE only. Whether the IRS has actually issued this GIIN is not knowable from here,
    /// and a shape check is the honest limit of what this class can promise — hence
    /// <see cref="GiinIsWellFormed"/> rather than a name suggesting validity.
    /// </para>
    /// </summary>
    private static readonly Regex GiinPattern = new(
        @"^[0-9A-Z]{6}\.[0-9A-Z]{5}\.[A-Z]{2}\.[0-9]{3}$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>Platform default for the PEP post-mandate window, in months.</summary>
    public const int DefaultPepPostMandateMonths = 12;

    /// <summary>Platform default share of automatic approvals reviewed afterwards (KRM-B-11).</summary>
    public const int DefaultKycAutoApprovalSamplePct = 10;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>
    /// False by default, and that default is load-bearing: a tenant with no row and a tenant that
    /// has never enabled FATCA must behave identically — no field required, none exposed, and none
    /// counted in a lead's completeness.
    /// </summary>
    public bool FatcaEnabled { get; private set; }

    /// <summary>Null while <see cref="FatcaEnabled"/> is false.</summary>
    public FatcaRegime? FatcaRegime { get; private set; }

    /// <summary>The institution's Global Intermediary Identification Number. Null while disabled.</summary>
    public string? FatcaGiin { get; private set; }

    public FatcaRequiredAt FatcaRequiredAt { get; private set; } = Domain.FatcaRequiredAt.Conversion;

    /// <summary>
    /// Whether FATCA indicia are collected from co-holders too, not only from the principal.
    /// True by default: a joint account's co-holder is as reportable as its holder, and defaulting
    /// this to false would silently narrow the obligation.
    /// </summary>
    public bool FatcaForCoHolders { get; private set; } = true;

    /// <summary>
    /// Whether politically-exposed-person vigilance is active for this tenant.
    ///
    /// <para>
    /// <b>TRUE by default, and that default is the load-bearing half.</b> A tenant with no row and
    /// a tenant that has never touched the screen must both keep vigilance ON: defaulting to false
    /// would switch off an LBC/FT obligation for every institution already in the database, as a
    /// side effect of a deployment. The migration adds the column with the same default for the
    /// same reason.
    /// </para>
    /// </summary>
    public bool PepEnabled { get; private set; } = true;

    /// <summary>
    /// Months a person stays politically exposed after the function ends (BNF-B-05). Read by
    /// <c>PepStatus.IsExposedAt</c>, which is the single definition of the window.
    ///
    /// <para>
    /// Kept when <see cref="PepEnabled"/> is false rather than cleared — unlike the FATCA identity,
    /// which is cleared on disable. The asymmetry is deliberate: a GIIN is an identifier whose
    /// value may have changed by the time somebody re-enables, while a window is a policy choice
    /// that is still the institution's own when it comes back. Clearing it would make a re-enable
    /// silently adopt the platform default.
    /// </para>
    /// </summary>
    public int PepPostMandateMonths { get; private set; } = DefaultPepPostMandateMonths;

    // ── Remote KYC: the tenant's ceiling on machine approval (KRM-B-01) ─────
    //
    // These three live HERE and not on the product, and not in M02's kyc_settings either, and the
    // split is the point of the feature. A product says how much control ITS risk deserves; the
    // institution says whether a KYC file may ever be validated without a human at all. M02
    // enforces the ceiling and M12 refuses to save a product that exceeds it, so both modules read
    // the same row — which is exactly what compliance_settings is for (M13, M01 and M02 already
    // read it). Putting them in kyc_settings would have left M12 unable to validate the product
    // screen: this assembly cannot reference M02.

    /// <summary>
    /// Whether any product of this tenant may be configured to approve a remote KYC file
    /// automatically.
    ///
    /// <para>
    /// <b>FALSE by default, and unlike <see cref="PepEnabled"/> that default is not a courtesy.</b>
    /// Today every KYC file passes through at least two humans (agent, then branch manager).
    /// Automatic approval replaces that circuit with a decision by the SYSTEM account, which is a
    /// compliance posture the institution's own responsible officer has to adopt deliberately — no
    /// deployment may adopt it on their behalf. An outage reading this flag answers false for the
    /// same reason.
    /// </para>
    /// </summary>
    public bool KycAutoApprovalEnabled { get; private set; }

    /// <summary>
    /// The highest KYC tier the machine may grant on its own — a <c>KycRemotePolicyValues</c> tier
    /// name, <see cref="KycRemotePolicyValues.SimplifiedTier"/> by default.
    ///
    /// <para>
    /// Defaulting to Simplified is the recommendation made to every institution: the simplified
    /// tier is capped (250 000 / 500 000 FCFA by default), so a machine decision that turns out
    /// wrong is bounded by those ceilings. Raising it to Full hands the machine an uncapped
    /// relationship, and that is the responsible officer's call to make and to own.
    /// </para>
    /// </summary>
    public string KycAutoApprovalMaxTier { get; private set; } = KycRemotePolicyValues.SimplifiedTier;

    /// <summary>
    /// Percentage of automatically approved files drawn for a compliance review after the fact
    /// (KRM-B-11). 10 by default; 0 switches the sampling off.
    /// </summary>
    public int KycAutoApprovalSamplePct { get; private set; } = DefaultKycAutoApprovalSamplePct;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }

    /// <summary>
    /// PostgreSQL xmin. Enabling FATCA and setting the GIIN are two fields of one decision; a lost
    /// update between them leaves the institution enabled with somebody else's identifier.
    /// </summary>
    public uint Version { get; private set; }

    private ComplianceSettings() { } // EF Core

    /// <summary>
    /// The row a tenant gets on first access: FATCA off, PEP vigilance ON, window at the platform
    /// default.
    /// Materialised lazily rather than seeded, for the same reason as
    /// <see cref="TenantNotificationSettings"/> — the defaults ARE the behaviour, so a missing row
    /// is correct rather than incomplete.
    /// </summary>
    public static ComplianceSettings CreateDefault(Guid tenantId, Guid createdBy, DateTimeOffset now) => new()
    {
        Id                   = Guid.NewGuid(),
        TenantId             = tenantId,
        FatcaEnabled         = false,
        FatcaRegime          = null,
        FatcaGiin            = null,
        FatcaRequiredAt      = Domain.FatcaRequiredAt.Conversion,
        FatcaForCoHolders    = true,
        PepEnabled           = true,
        PepPostMandateMonths = DefaultPepPostMandateMonths,
        // Machine approval off, and capped at the simplified tier if it is ever turned on.
        KycAutoApprovalEnabled   = false,
        KycAutoApprovalMaxTier   = KycRemotePolicyValues.SimplifiedTier,
        KycAutoApprovalSamplePct = DefaultKycAutoApprovalSamplePct,
        CreatedAt            = now,
        UpdatedAt            = now,
        UpdatedBy            = createdBy,
    };

    public static bool GiinIsWellFormed(string? giin)
        => !string.IsNullOrWhiteSpace(giin) && GiinPattern.IsMatch(giin.Trim().ToUpperInvariant());

    /// <summary>
    /// Applies a complete FATCA configuration. All-or-nothing by design: the regime and the GIIN
    /// only mean anything together, so there is no setter that can leave the pair half-applied.
    /// </summary>
    /// <returns>
    /// <c>FATCA_REGIME_REQUIRED</c> / <c>FATCA_GIIN_REQUIRED</c> / <c>FATCA_GIIN_INVALID</c> when
    /// enabling without a complete, well-formed identity.
    /// </returns>
    public Result ConfigureFatca(
        bool enabled,
        FatcaRegime? regime,
        string? giin,
        FatcaRequiredAt requiredAt,
        bool forCoHolders,
        Guid actor,
        DateTimeOffset now)
    {
        if (enabled)
        {
            // Enabling is the gate, not saving. An institution that declares itself subject to
            // FATCA without a regime and a GIIN has published an obligation it cannot discharge:
            // every declaration collected from then on is unreportable.
            if (regime is null)
                return Result.Fail("FATCA_REGIME_REQUIRED");
            if (string.IsNullOrWhiteSpace(giin))
                return Result.Fail("FATCA_GIIN_REQUIRED");
            if (!GiinIsWellFormed(giin))
                return Result.Fail("FATCA_GIIN_INVALID");

            FatcaEnabled = true;
            FatcaRegime  = regime;
            FatcaGiin    = giin.Trim().ToUpperInvariant();
        }
        else
        {
            // Disabling clears the identity rather than keeping it dormant. A stale GIIN left in
            // the row is one a later re-enable would adopt silently, and the institution's
            // registration may well have changed in between.
            FatcaEnabled = false;
            FatcaRegime  = null;
            FatcaGiin    = null;
        }

        FatcaRequiredAt   = requiredAt;
        FatcaForCoHolders = forCoHolders;

        Stamp(actor, now);
        return Result.Ok();
    }

    /// <summary>
    /// Applies the PEP configuration: whether vigilance is active, and the post-mandate window.
    ///
    /// <para>
    /// One method for the pair, like <see cref="ConfigureFatca"/>, so a caller cannot turn
    /// vigilance off in one request and set a window in another. The window is still validated
    /// when vigilance is off: a value stored now is the one a re-enable will use, and accepting
    /// nonsense on the way down only defers the refusal to the moment somebody turns it back on.
    /// </para>
    /// </summary>
    /// <returns><c>PEP_POST_MANDATE_MONTHS_INVALID</c> outside 0..600 (fifty years).</returns>
    public Result ConfigurePep(bool enabled, int months, Guid actor, DateTimeOffset now)
    {
        var window = SetPepPostMandateMonths(months, actor, now);
        if (window.IsFailure) return window;

        PepEnabled = enabled;
        Stamp(actor, now);

        return Result.Ok();
    }

    /// <summary>
    /// Sets the PEP post-mandate window.
    /// </summary>
    /// <returns><c>PEP_POST_MANDATE_MONTHS_INVALID</c> outside 0..600 (fifty years).</returns>
    public Result SetPepPostMandateMonths(int months, Guid actor, DateTimeOffset now)
    {
        // Zero is legitimate — "exposed only while in office" — so the floor is 0 and not 1. The
        // ceiling exists so a typo in a form cannot make every former office holder permanently
        // exposed without anybody noticing the value.
        if (months is < 0 or > 600)
            return Result.Fail("PEP_POST_MANDATE_MONTHS_INVALID");

        PepPostMandateMonths = months;
        Stamp(actor, now);
        return Result.Ok();
    }

    /// <summary>
    /// Applies the tenant's position on automatic KYC approval (KRM-B-01): whether the machine may
    /// decide at all, the highest tier it may grant, and how much of its output is reviewed
    /// afterwards.
    ///
    /// <para>
    /// One method for the three, like <see cref="ConfigureFatca"/>: a ceiling raised in one request
    /// and the switch flipped in another leaves a window in which the machine grants more than the
    /// officer intended. The ceiling and the sample rate are validated even while the switch is
    /// off, because the values stored now are the ones a later enable will use.
    /// </para>
    ///
    /// <para>
    /// <b>It does not relax any product.</b> Turning the switch off stops new products being saved
    /// as Automatic and makes M02 route every file to a human; it does NOT rewrite a product that
    /// was already configured Automatic, and it does not un-approve a file the machine has already
    /// decided. The product keeps saying what the institution once wanted, and the gate simply
    /// stops honouring it — which is also what lets the officer turn the feature off in one click
    /// without losing a catalogue's worth of configuration.
    /// </para>
    /// </summary>
    /// <returns>
    /// <c>KYC_AUTO_APPROVAL_MAX_TIER_INVALID</c> when the tier is not a known one, and
    /// <c>KYC_AUTO_APPROVAL_SAMPLE_PCT_INVALID</c> outside 0..100.
    /// </returns>
    public Result ConfigureKycAutoApproval(
        bool enabled, string? maxTier, int samplePct, Guid actor, DateTimeOffset now)
    {
        var tier = KycRemotePolicyValues.NormalizeTier(maxTier);
        if (tier is null)
            return Result.Fail("KYC_AUTO_APPROVAL_MAX_TIER_INVALID");

        // Zero is legitimate — "no sampling" — so the floor is 0 and not 1.
        if (samplePct is < 0 or > 100)
            return Result.Fail("KYC_AUTO_APPROVAL_SAMPLE_PCT_INVALID");

        KycAutoApprovalEnabled = enabled;
        KycAutoApprovalMaxTier = tier;
        KycAutoApprovalSamplePct = samplePct;

        Stamp(actor, now);
        return Result.Ok();
    }

    private void Stamp(Guid actor, DateTimeOffset now)
    {
        UpdatedBy = actor;
        UpdatedAt = now;
    }
}

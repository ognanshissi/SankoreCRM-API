namespace Sankore.Modules.Administration.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Whether a product line may be KYC'd REMOTELY after a conversion, and how such a file is
/// validated (KRM-B-01). Stored as a typed <c>jsonb</c> column on
/// <see cref="ProductSpeciality"/> and read by M02 when a converted lead's file is opened.
///
/// <para>
/// <b>Why it sits on the product and not in a tenant setting.</b> The question it answers is "for
/// THIS product": a current account sold at 5 000 FCFA is not a mortgage, and the level of control
/// a remote enrolment deserves follows the risk of what is being sold. A per-tenant switch could
/// only say yes or no for the whole catalogue, which is how a credit ends up validated by the same
/// machine decision as a savings account. The tenant does keep one word in the matter, and it is a
/// CEILING rather than a policy — see <see cref="ComplianceSettings.KycAutoApprovalMaxTier"/>.
/// </para>
///
/// <para>
/// <b>Why the enum-shaped members are strings.</b> <see cref="ValidationMode"/> is this module's
/// own concept, but <see cref="RequiredTier"/>, <see cref="RequiredDocuments"/> and
/// <see cref="AllowedDocumentTypes"/> name M02 values (<c>KycTier</c>, <c>KycDocumentKind</c>,
/// <c>KycDocumentType</c>) and M12 may not reference another module. They are validated against a
/// closed list here and compared by NAME at the gate — the same arrangement
/// <see cref="BeneficiaryPolicy.RequiredKycTier"/> already uses, and for the same reason: a typo
/// must be refused at the screen that saved it, not discovered weeks later by a file that routed
/// the wrong way.
/// </para>
///
/// <para>
/// <b>Why a record and not columns.</b> Seven optional, product-shaped fields that M02 reads WHOLE
/// and never filters on. Nothing queries "which products allow remote KYC" — the question is
/// always "what does this line say", asked once per conversion with the line id in hand.
/// </para>
/// </summary>
/// <param name="Enabled">
/// Whether a conversion on this line sends the new customer a remote-collection link at all.
/// <c>false</c> by default, and that default is load-bearing: enabling remote enrolment publishes
/// a public page that collects a customer's identity document, and no deployment may acquire one
/// as a side effect of an upgrade.
/// </param>
/// <param name="ValidationMode">
/// <c>"Manual"</c> (default) or <c>"Automatic"</c>. Automatic only ever APPROVES — a file that
/// fails any one of the conditions routes to a human with the reason attached, and nothing here
/// can cause a rejection. See KRM-B-08.
/// </param>
/// <param name="RequiredTier">
/// <c>"Simplified"</c> or <c>"Full"</c> — the KYC level this line requires. Null means the line
/// states no level of its own and the tenant's baseline applies.
/// <para>
/// Distinct from <see cref="BeneficiaryPolicy.RequiredKycTier"/>, which gates a CONVERSION: this
/// one says what the remote collection must end up granting. A line can perfectly well convert on
/// a simplified file and require a full one before it pays out.
/// </para>
/// </param>
/// <param name="MinAutoScore">
/// Confidence score, 0-100, below which a file is never approved automatically. Default 85.
/// Meaningless while <see cref="ValidationMode"/> is Manual, and still validated then: the value
/// stored now is the one switching to Automatic will use.
/// </param>
/// <param name="RequiredDocuments">
/// <c>KycDocumentKind</c> names the customer must have sent before the file may be submitted —
/// <c>ProofOfAddress</c> for an insurance product sold at a distance (CIMA LBC/FT article 13.6).
/// The identity document front and the selfie are not listed here: they are what a remote
/// enrolment IS, and a line that omitted them would collect nothing.
/// </param>
/// <param name="AllowedDocumentTypes">
/// <c>KycDocumentType</c> names this line accepts as an identity document. Never empty — a line
/// that accepts no document cannot be enrolled remotely at all, and an empty list reads as
/// "unconfigured" to every screen, which is exactly the ambiguity that makes a customer reach a
/// page with no option to pick.
/// </param>
/// <param name="LinkValidityHours">How long the link stays usable. Default 72.</param>
public sealed record KycRemotePolicy(
    bool Enabled = false,
    string ValidationMode = KycRemotePolicyValues.ManualMode,
    string? RequiredTier = null,
    int MinAutoScore = KycRemotePolicyValues.DefaultMinAutoScore,
    IReadOnlyList<string>? RequiredDocuments = null,
    IReadOnlyList<string>? AllowedDocumentTypes = null,
    int LinkValidityHours = KycRemotePolicyValues.DefaultLinkValidityHours)
{
    /// <summary>
    /// What a line carries until an administrator configures one: remote collection OFF, manual
    /// validation, the platform's default score floor and link lifetime.
    ///
    /// <para>
    /// The restrictive reading of "unconfigured", like <see cref="BeneficiaryPolicy.None"/>. Note
    /// that <see cref="AllowedDocumentTypes"/> is empty here and that this is NOT a contradiction
    /// with <see cref="Create"/> refusing an empty list: a disabled policy is never read for its
    /// document types, and requiring one on a line nobody has set up would mean inventing an
    /// acceptance the institution never declared.
    /// </para>
    /// </summary>
    public static KycRemotePolicy Disabled { get; } = new();

    public IReadOnlyList<string> Documents => RequiredDocuments ?? [];

    public IReadOnlyList<string> DocumentTypes => AllowedDocumentTypes ?? [];

    /// <summary>
    /// True when this line wants the machine to be able to approve on its own. Read as a question
    /// about the POLICY only — whether a given file may actually be approved automatically is
    /// M02's decision and also depends on the tenant ceiling, the score and the flags.
    /// </summary>
    public bool WantsAutomaticValidation
        => string.Equals(ValidationMode, KycRemotePolicyValues.AutomaticMode,
               StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validates and normalises a policy an administrator submitted.
    /// </summary>
    /// <param name="autoApprovalEnabledForTenant">
    /// Whether the tenant allows automatic approval at all
    /// (<see cref="ComplianceSettings.KycAutoApprovalEnabled"/>). Refused HERE rather than left to
    /// M02: a product saved as Automatic on a tenant that forbids it would look configured on the
    /// screen and behave manually in production, which is the one discrepancy an administrator
    /// cannot debug.
    /// </param>
    /// <param name="autoApprovalMaxTier">
    /// The highest tier the tenant lets the machine grant
    /// (<see cref="ComplianceSettings.KycAutoApprovalMaxTier"/>).
    /// </param>
    /// <returns>
    /// <c>KYC_REMOTE_VALIDATION_MODE_INVALID</c>, <c>KYC_REMOTE_TIER_INVALID</c>,
    /// <c>KYC_REMOTE_AUTO_APPROVAL_DISABLED</c>, <c>KYC_REMOTE_TIER_ABOVE_AUTO_LIMIT</c>,
    /// <c>KYC_REMOTE_MIN_SCORE_INVALID</c>, <c>KYC_REMOTE_LINK_VALIDITY_INVALID</c>,
    /// <c>KYC_REMOTE_DOCUMENT_KIND_INVALID:&lt;value&gt;</c>,
    /// <c>KYC_REMOTE_DOCUMENT_TYPE_INVALID:&lt;value&gt;</c>, or
    /// <c>KYC_REMOTE_DOCUMENT_TYPES_REQUIRED</c>.
    /// </returns>
    public static Result<KycRemotePolicy> Create(
        bool enabled,
        string? validationMode,
        string? requiredTier,
        int minAutoScore,
        IReadOnlyList<string>? requiredDocuments,
        IReadOnlyList<string>? allowedDocumentTypes,
        int linkValidityHours,
        bool autoApprovalEnabledForTenant,
        string autoApprovalMaxTier)
    {
        var mode = KycRemotePolicyValues.NormalizeMode(validationMode);
        if (mode is null)
            return Result.Fail<KycRemotePolicy>("KYC_REMOTE_VALIDATION_MODE_INVALID");

        var tier = KycRemotePolicyValues.NormalizeTier(requiredTier);
        if (tier is null && !string.IsNullOrWhiteSpace(requiredTier))
            return Result.Fail<KycRemotePolicy>("KYC_REMOTE_TIER_INVALID");

        var automatic = string.Equals(
            mode, KycRemotePolicyValues.AutomaticMode, StringComparison.Ordinal);

        if (automatic)
        {
            if (!autoApprovalEnabledForTenant)
                return Result.Fail<KycRemotePolicy>("KYC_REMOTE_AUTO_APPROVAL_DISABLED");

            // Compared against the tenant's ceiling only when the line states a tier. A line with
            // no tier of its own grants the tenant's baseline, which is the ceiling's own concern
            // and not this one's.
            if (tier is not null && !KycRemotePolicyValues.TierWithinCeiling(tier, autoApprovalMaxTier))
                return Result.Fail<KycRemotePolicy>("KYC_REMOTE_TIER_ABOVE_AUTO_LIMIT");
        }

        // Validated whatever the mode, like the PEP window on ComplianceSettings: accepting
        // nonsense while Manual only defers the refusal to the moment somebody switches the line
        // to Automatic, by which time nobody remembers typing it.
        if (minAutoScore is < KycRemotePolicyValues.MinimumAutoScoreFloor or > 100)
            return Result.Fail<KycRemotePolicy>("KYC_REMOTE_MIN_SCORE_INVALID");

        if (linkValidityHours is < 1 or > KycRemotePolicyValues.MaximumLinkValidityHours)
            return Result.Fail<KycRemotePolicy>("KYC_REMOTE_LINK_VALIDITY_INVALID");

        var documents = new List<string>();
        foreach (var raw in requiredDocuments ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var kind = KycRemotePolicyValues.NormalizeDocumentKind(raw);
            if (kind is null)
                return Result.Fail<KycRemotePolicy>(
                    $"KYC_REMOTE_DOCUMENT_KIND_INVALID:{raw.Trim()}");

            if (!documents.Contains(kind, StringComparer.Ordinal))
                documents.Add(kind);
        }

        var types = new List<string>();
        foreach (var raw in allowedDocumentTypes ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var type = KycRemotePolicyValues.NormalizeDocumentType(raw);
            if (type is null)
                return Result.Fail<KycRemotePolicy>(
                    $"KYC_REMOTE_DOCUMENT_TYPE_INVALID:{raw.Trim()}");

            if (!types.Contains(type, StringComparer.Ordinal))
                types.Add(type);
        }

        // Only an ENABLED line has to name the documents it accepts. Requiring it while disabled
        // would force an administrator to declare an acceptance in order to turn the feature off.
        if (enabled && types.Count == 0)
            return Result.Fail<KycRemotePolicy>("KYC_REMOTE_DOCUMENT_TYPES_REQUIRED");

        return Result.Ok(new KycRemotePolicy(
            Enabled: enabled,
            ValidationMode: mode,
            RequiredTier: tier,
            MinAutoScore: minAutoScore,
            RequiredDocuments: documents,
            AllowedDocumentTypes: types,
            LinkValidityHours: linkValidityHours));
    }
}

/// <summary>
/// The closed lists <see cref="KycRemotePolicy"/> validates against, and the one place their
/// spelling lives.
///
/// <para>
/// Four of them mirror an M02 enum that this assembly cannot reference
/// (<c>KycTier</c>, <c>KycDocumentKind</c>, <c>KycDocumentType</c>). That duplication is the price
/// of the module boundary and it is paid deliberately — the alternative is a free-text column
/// whose typos only surface in production. A value added to one of M02's enums has to be added
/// here too, and <c>KycRemotePolicyValuesTests</c> is what will say so.
/// </para>
/// </summary>
public static class KycRemotePolicyValues
{
    public const string ManualMode = "Manual";
    public const string AutomaticMode = "Automatic";

    public const string SimplifiedTier = "Simplified";
    public const string FullTier = "Full";

    public const int DefaultMinAutoScore = 85;
    public const int DefaultLinkValidityHours = 72;

    /// <summary>
    /// Floor of <c>MinAutoScore</c>. Not 0: a threshold of zero approves every answer the scoring
    /// service produces, which is indistinguishable from having no threshold while looking like a
    /// configured one.
    /// </summary>
    public const int MinimumAutoScoreFloor = 50;

    /// <summary>Two weeks. A link that outlives its customer's memory of asking for it is a liability.</summary>
    public const int MaximumLinkValidityHours = 336;

    public static readonly IReadOnlyList<string> Modes = [ManualMode, AutomaticMode];

    public static readonly IReadOnlyList<string> Tiers = [SimplifiedTier, FullTier];

    /// <summary>M02's <c>KycDocumentKind</c>, by name.</summary>
    public static readonly IReadOnlyList<string> DocumentKinds =
    [
        "IdentityDocumentFront",
        "IdentityDocumentBack",
        "Selfie",
        "ProofOfAddress",
    ];

    /// <summary>M02's <c>KycDocumentType</c>, by name.</summary>
    public static readonly IReadOnlyList<string> DocumentTypes =
    [
        "Cni",
        "Passport",
        "Cedeao",
        "Consulaire",
    ];

    public static string? NormalizeMode(string? value)
        => Match(Modes, value) ?? (string.IsNullOrWhiteSpace(value) ? ManualMode : null);

    public static string? NormalizeTier(string? value) => Match(Tiers, value);

    public static string? NormalizeDocumentKind(string? value) => Match(DocumentKinds, value);

    public static string? NormalizeDocumentType(string? value) => Match(DocumentTypes, value);

    /// <summary>
    /// Whether <paramref name="tier"/> is at or below <paramref name="ceiling"/>. Ordered by the
    /// position in <see cref="Tiers"/>, which is ordered by how much the level permits —
    /// Simplified before Full.
    /// </summary>
    /// <remarks>
    /// An unrecognised ceiling answers <c>false</c>. Reading it as "no ceiling" would turn a
    /// corrupted row into permission to grant the full tier without a human, and a compliance gate
    /// must fail closed.
    /// </remarks>
    public static bool TierWithinCeiling(string? tier, string? ceiling)
    {
        var normalizedTier = NormalizeTier(tier);
        var normalizedCeiling = NormalizeTier(ceiling);

        if (normalizedTier is null || normalizedCeiling is null)
            return false;

        return IndexOfTier(normalizedTier) <= IndexOfTier(normalizedCeiling);
    }

    private static int IndexOfTier(string tier)
    {
        for (var i = 0; i < Tiers.Count; i++)
        {
            if (string.Equals(Tiers[i], tier, StringComparison.Ordinal))
                return i;
        }

        return int.MaxValue;
    }

    /// <summary>
    /// The canonical spelling of <paramref name="value"/> in <paramref name="allowed"/>, or null.
    /// Case-insensitive on the way in and canonical on the way out, so a stored policy holds one
    /// spelling per value and no reader has to care how an administrator typed it.
    /// </summary>
    private static string? Match(IReadOnlyList<string> allowed, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var probe = value.Trim();

        foreach (var candidate in allowed)
        {
            if (string.Equals(candidate, probe, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }
}

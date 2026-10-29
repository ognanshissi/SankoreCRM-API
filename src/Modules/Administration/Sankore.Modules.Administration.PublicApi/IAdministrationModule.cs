using Sankore.Shared.Kernel.ValueObject;

namespace Sankore.Modules.Administration.PublicApi;

using Sankore.Shared.Kernel;

/// <summary>
/// The ONLY entry point other modules (e.g. Leads) are allowed to use to
/// read data owned by the Users module. No other module may reference
/// Sankore.Modules.Administration directly or its DbContext — only this interface
/// and the DTOs below, both living in this small, dependency-free assembly.
/// </summary>
public interface IAdministrationModule
{
    /// <summary>
    /// Returns commercial agents currently available for lead dispatching,
    /// optionally filtered to a specific agency. Used by
    /// Leads.Features.DispatchLead.DispatchLeadHandler.
    /// </summary>
    Task<IReadOnlyList<AgentSummary>> GetAvailableAgentsAsync(
        Guid tenantId,
        Guid? agencyId,
        CancellationToken ct);

    Task<AgentSummary?> GetAgentAsync(Guid agentId, CancellationToken ct);

    /// <summary>
    /// The agency one user belongs to, or <c>null</c> (TEL-B-19).
    ///
    /// <para>
    /// <paramref name="tenantId"/> is explicit, and that is the whole reason this exists next to
    /// <see cref="GetAgentAsync"/>: the caller is a webhook handler or a Hangfire job, where the
    /// ambient tenant context is not the tenant being operated on — the telephony webhook route
    /// is deliberately exempt from host resolution, so it derives its tenant from the connection
    /// and carries it by hand. <c>GetAgentAsync</c> leans on the ambient filter and would answer
    /// nothing, or the wrong tenant's user, from there.
    /// </para>
    ///
    /// <para>
    /// <b>Three situations answer <c>null</c> and the caller must treat them identically:</b> the
    /// user does not exist, the user belongs to another tenant, or the user genuinely has no
    /// agency — which M12 permits and documents as ordinary for a head-office account. The
    /// meaning the caller may rely on is "outside every restricted perimeter", never "inside all
    /// of them". <c>Guid.Empty</c> is never returned, unlike <c>AgentSummary.AgencyId</c> which
    /// flattens the absent case to it.
    /// </para>
    /// </summary>
    Task<Guid?> GetUserAgencyAsync(Guid tenantId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Non-sensitive projection of one agency, or <c>null</c> when it does not exist
    /// (or has been soft-deleted) in that tenant.
    ///
    /// Added for module M01 (Customers), which stamps the agency CODE onto every
    /// client record so a client number can be minted per agency and per year
    /// (<c>{AgencyCode}-{YYYY}-{Seq:6}</c>) without M01 ever reading the
    /// <c>administration</c> schema. <c>AgentSummary</c> only carries an agency id,
    /// hence this second, agency-shaped projection.
    ///
    /// <paramref name="tenantId"/> is explicit because callers may run outside an HTTP
    /// request (Hangfire jobs, MassTransit consumers) where the ambient tenant context
    /// is not the tenant being operated on.
    /// </summary>
    Task<AgencySummary?> GetAgencyAsync(Guid tenantId, Guid agencyId, CancellationToken ct);

    /// <summary>
    /// Returns the IDs of agents managed by the given supervisor (same agency or sub-agencies).
    /// Used for task visibility scoping (US-M13-090).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetTeamAgentIdsAsync(Guid tenantId, Guid supervisorId, CancellationToken ct);

    /// <summary>
    /// Who should be TOLD about something that happened in one of the tenant's agencies
    /// (KRM-B-09): the preferred user when they can still act, otherwise the agency's manager,
    /// otherwise nobody.
    ///
    /// <para>
    /// <b>A resolution, not a lookup, and it belongs here.</b> "Active" is <c>AppUser.Status</c>
    /// and "attached to the agency" is <c>AppUser.AgencyId</c>; "the branch's chief" is
    /// <c>Agency.ManagerUserId</c>. All three are M12's, none of them is on
    /// <see cref="AgentSummary"/>, and a caller handed the three parts would re-implement the
    /// cascade — differently each time, and wrongly the first time somebody forgets that a
    /// disabled account still has a mailbox the mail provider will accept.
    /// </para>
    ///
    /// <para>
    /// Deliberately named for a RECIPIENT and not for an approver: it answers who receives a
    /// message, never who is entitled to decide anything. Authorisation stays with the
    /// authorization pipeline and the agency perimeter.
    /// </para>
    ///
    /// <para>
    /// <b><c>null</c> is an ordinary answer</b> and every caller must handle it: an agency with no
    /// manager and a departed agent is a real configuration. The thing being notified about is
    /// still visible in the back office — a notification is a convenience, never the only way
    /// work surfaces.
    /// </para>
    ///
    /// <para>
    /// <b>What this does NOT do</b>, and the specification asks for it: fall back to "la file de
    /// l'agence". This platform has no agency distribution list and no shared mailbox — there is
    /// nothing to address — so the cascade stops at the manager rather than inventing a recipient.
    /// Adding one is a feature in M12, not a branch here.
    /// </para>
    /// </summary>
    /// <param name="agencyId">
    /// The agency the subject belongs to — a client's agency, a file's agency. The preferred user
    /// is only accepted when they are attached to THIS agency: an agent who moved branch keeps
    /// their account and should not keep receiving another branch's work.
    /// </param>
    /// <param name="preferredUserId">
    /// The first choice — the agent accountable for the subject. Null skips straight to the
    /// manager.
    /// </param>
    Task<Guid?> ResolveAgencyRecipientAsync(
        Guid tenantId,
        Guid agencyId,
        Guid? preferredUserId,
        CancellationToken ct);

    /// <summary>
    /// Who answers for a user: their line manager (<c>ReportsToUserId</c>, « le responsable ») and
    /// the head of their agency (« le chef d'agence »). Either is null when unset or no longer an
    /// active account of the tenant. Used to let those two people into a task the user holds.
    /// </summary>
    Task<UserSupervisors> GetUserSupervisorsAsync(Guid tenantId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Le nom affiché de chaque utilisateur demandé, en un appel — pour une liste qui montre un
    /// conseiller par ligne. Un identifiant inconnu (ou d'un autre tenant) est absent du résultat.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> userIds, CancellationToken ct);

    /// <summary>
    /// The tenant's users whose address is one of these — the reverse of
    /// <see cref="GetUserNamesAsync"/>, for pairing an external directory against CRM accounts
    /// (TEL-B-05).
    ///
    /// <para>
    /// A user's e-mail is NOT encrypted in this module (Identity owns it, and sign-in matches it
    /// in SQL), so this is an ordinary indexed lookup and not the clear-in/ids-out arrangement
    /// M01's <c>FindMatchesAsync</c> needs. Comparison is case-insensitive, because a provider's
    /// directory capitalises an address however the person typed it when they signed up.
    /// </para>
    ///
    /// <para>
    /// <see cref="UserEmailMatch.IsActive"/> travels with each row rather than being filtered out
    /// here: a caller proposing a pairing has to be able to say "that account is disabled" instead
    /// of showing the external user as unmatched, which reads as a missing CRM account and sends
    /// an administrator to create a second one.
    /// </para>
    ///
    /// <para>
    /// An address nobody holds is simply absent, and so is a blank one. Empty when nothing
    /// matches, never null.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<UserEmailMatch>> FindUsersByEmailAsync(
        Guid tenantId, IReadOnlyCollection<string> emails, CancellationToken ct);

    /// <summary>
    /// Returns the product category (Loan, Savings, Tontine) for a given product code.
    /// Used by Leads module for qualification template resolution.
    /// Returns null if the product code is not found.
    /// </summary>
    Task<string?> GetProductCategoryAsync(Guid tenantId, string productCode, CancellationToken ct);

    /// <summary>
    /// One catalogue entry by its code, or <c>null</c> when this tenant has no such product.
    ///
    /// <para>
    /// The second, product-shaped projection next to <see cref="GetProductCategoryAsync"/>, for
    /// the reason <see cref="GetAgencyAsync"/> exists next to <see cref="GetAgentAsync"/>: a
    /// consumer that has to decide whether a product may still be SOLD needs its activity flag,
    /// and a category string cannot carry one. M-Integration's insurance catalogue is the first
    /// caller — a withdrawn catalogue entry must stop new subscriptions at the counter — and
    /// reading the flag through a second call to the category lookup is not possible.
    /// </para>
    ///
    /// <para>
    /// The code is matched the way M12 stores it: upper-cased. A caller passes whatever it holds.
    /// </para>
    /// </summary>
    Task<ProductSummary?> GetProductAsync(Guid tenantId, string productCode, CancellationToken ct);

    /// <summary>
    /// Every product of the tenant, newest-sorted by code, optionally narrowed to one category.
    ///
    /// <para>
    /// The ENUMERATION the contract was missing. Two callers need it and neither can be served by
    /// a lookup that takes the code it would have to return: M-Integration's "which CRM codes are
    /// still unmapped" screen (INT-04), which until now reported the <c>Product</c> domain as
    /// unavailable, and any screen offering an administrator the catalogue entries an insurance
    /// product may be attached to.
    /// </para>
    ///
    /// <para>
    /// <paramref name="category"/> is a <see cref="ProductCategory"/> name, compared
    /// case-insensitively; an unrecognised value returns an EMPTY list rather than every product,
    /// because a filter silently ignored is how a caller asking for insurance products gets loans.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<ProductSummary>> ListProductsAsync(
        Guid tenantId, string? category, CancellationToken ct);

    /// <summary>
    /// The tenant's form for a lead, a client or a product — native fields as laid out, plus its
    /// custom fields (dynamic forms, lot 1). Never null: a tenant that never saved a layout gets
    /// the default one, version 0.
    ///
    /// <para>
    /// Read by the module that stores the record, to check its custom values with
    /// <see cref="Forms.CustomFieldValues.Apply"/> and the native fields the tenant made required
    /// with <see cref="Forms.CustomFieldValues.MissingRequiredNatives"/>. Takes the tenant
    /// explicitly because consumers and import jobs call it outside a request.
    /// </para>
    /// </summary>
    Task<Forms.EntityFormDto> GetEntityFormAsync(Guid tenantId, Forms.FormEntity entity, CancellationToken ct);

    /// <summary>
    /// Returns the email provider configuration for a tenant so that the
    /// Notifications module can resolve the correct provider at send time.
    /// Returns null when no custom config exists (use platform default).
    /// </summary>
    Task<TenantNotificationConfigDto?> GetNotificationConfigAsync(
        Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Counts one email against the tenant's monthly allowance, or refuses it. Check and
    /// increment happen together so two concurrent senders cannot both slip past the limit.
    /// A tenant with no configured limit is always granted — and still counted, so usage stays
    /// visible before anyone sets one.
    /// </summary>
    Task<EmailQuotaDecision> TryConsumeEmailQuotaAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// The tenant's SMS channel, or <c>null</c> when it has never been configured.
    ///
    /// <para>
    /// <b>Null means "no SMS channel", and unlike the email configuration there is no platform
    /// fallback to substitute.</b> An email can always go out through the platform's own SMTP
    /// account; a text message cannot, because somebody pays per message and no platform-wide
    /// account exists. Every caller therefore has to handle null as "cannot send", which is why
    /// this one returns null where <see cref="GetNotificationConfigAsync"/>'s caller substitutes a
    /// default.
    /// </para>
    ///
    /// <para>
    /// It carries no credential — only whether one is stored. The sender fetches the secret from
    /// the vault at send time, and this DTO is cached.
    /// </para>
    /// </summary>
    Task<TenantSmsConfigDto?> GetSmsConfigAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Counts one text message against the tenant's monthly allowance, or refuses it.
    ///
    /// <para>
    /// <b>Its own allowance, separate from the email one.</b> A text message costs real money per
    /// unit where an email costs almost nothing, so the two budgets are not comparable — and a
    /// shared counter would let a month of emails exhaust the allowance a customer's one-time code
    /// needs. Refused for a tenant with no SMS row at all: no channel is no allowance.
    /// </para>
    ///
    /// <para>
    /// Check and increment are ONE operation under optimistic concurrency, exactly as
    /// <see cref="TryConsumeEmailQuotaAsync"/>: splitting them lets two senders both read
    /// "999 of 1000" and both send.
    /// </para>
    /// </summary>
    Task<SmsQuotaDecision> TryConsumeSmsQuotaAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Which tenant owns an SMS provider account, by the identifier the provider puts in its
    /// callbacks — Twilio's Account SID.
    ///
    /// <para>
    /// <b>It exists because a provider's delivery callback is anonymous and carries no tenant.</b>
    /// Twilio posts an <c>AccountSid</c> and nothing else that identifies anybody, so the receiving
    /// endpoint has to turn that into a tenant before it can even fetch the auth token its
    /// signature is verified with. The alternative — M08 reading
    /// <c>administration.tenant_sms_settings</c> directly — would be a cross-schema query, which
    /// this solution does not do even for a single column.
    /// </para>
    ///
    /// <para>
    /// <c>null</c> for an unknown SID, which an endpoint must treat as "ignore this callback"
    /// rather than as an error: it is either a stale setting at the provider or somebody else's
    /// traffic, and answering an error makes the provider retry it for hours.
    /// </para>
    ///
    /// <para>
    /// A LOOKUP BY SECRETLESS IDENTIFIER, deliberately: an Account SID authorises nothing on its
    /// own — it is the username half of Twilio's basic auth — so answering which tenant holds one
    /// leaks nothing a caller could use. The auth token stays in the vault.
    /// </para>
    /// </summary>
    Task<Guid?> ResolveTenantBySmsAccountSidAsync(string accountSid, CancellationToken ct);

    /// <summary>
    /// Sends a templated notification to one of the tenant's USERS — an agent, an advisor, a
    /// manager. Returns whether it was actually queued.
    ///
    /// <para>
    /// <b>A send, not an address lookup, and that is the whole point.</b> This module owns the
    /// user's email, their locale preference and the "mail server down must not undo the decision
    /// that triggered the mail" rule (<c>ModuleEmailSender</c>). Handing an address to another
    /// module would duplicate the locale fallback — which matters more than it looks, because
    /// template locales are resolved case-sensitively in PostgreSQL and a wrong one mails the
    /// message's own JSON payload as its body.
    /// </para>
    ///
    /// <para>
    /// Before this existed, modules notifying an agent had nowhere to get an address:
    /// <c>AgentSummary</c> carries none, and M13's task-assignment consumer was sending to
    /// <c>{agentId}@agent.internal</c> — a dead address, failing silently.
    /// </para>
    ///
    /// <para>
    /// <b>Never throws.</b> A user with no address, an unknown user and a failure to queue all
    /// return <c>false</c>, because every caller is a consumer or a job whose own work must stand
    /// whether or not the mail went out.
    /// </para>
    /// </summary>
    Task<bool> NotifyUserAsync(NotifyUserRequest request, CancellationToken ct);

    /// <summary>
    /// The institution's compliance parameters — FATCA regime and PEP post-mandate window
    /// (BNF-B-10, BNF-B-05).
    ///
    /// <para>
    /// <b>Never returns null.</b> A tenant that has never opened the compliance screen has no row,
    /// and the platform defaults ARE its configuration: FATCA off, PEP window 12 months. Returning
    /// null would hand every caller the same decision to make — "no row, so is FATCA on?" — and
    /// one of them would eventually answer it the wrong way. Callers get the effective values and
    /// nothing to interpret.
    /// </para>
    ///
    /// <para>
    /// Read by M13 (a lead's completeness and its PEP exposure), M01 (the client record) and M02
    /// (the vigilance level). Cheap enough to call per request: one indexed read on
    /// <c>(tenant_id)</c>.
    /// </para>
    /// </summary>
    Task<ComplianceSettingsDto> GetComplianceSettingsAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// The institution's standing as an insurance distributor on <paramref name="today"/>
    /// (insurance lot 2): its status and legal mention, the annual SMIG, and which required pieces
    /// are missing or expired. A new subscription is refused unless <c>InGoodStanding</c>.
    /// Never null: a tenant that never filled the profile is reported as not configured.
    /// </summary>
    Task<InsuranceDistributorStanding> GetInsuranceDistributorStandingAsync(
        Guid tenantId, DateOnly today, CancellationToken ct);

    /// <summary>
    /// The active users whose roles carry <paramref name="permissionCode"/> — who to tell when a
    /// deadline concerns a function rather than a person (a remittance, a compliance alert).
    /// </summary>
    /// <summary>
    /// The active users holding one of <paramref name="roleCodes"/> (compared case-insensitively),
    /// in <paramref name="agencyId"/> or one of its parent agencies — a branch manager hears about
    /// their branch, a head-office officer about every branch. Null agency = the whole tenant.
    /// For a workflow « Notification » step addressed to a role.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListActiveUsersInRolesAsync(
        Guid tenantId, IReadOnlyCollection<string> roleCodes, Guid? agencyId, CancellationToken ct);

    Task<IReadOnlyList<Guid>> ListUsersWithPermissionAsync(
        Guid tenantId, string permissionCode, CancellationToken ct);

    /// <summary>
    /// The beneficiary policy of a product line (BNF-B-06) — what it asks of the household and of
    /// the people designated on it. Read by M13 (a lead's completeness and the household gate) and
    /// by M01 (designations).
    /// </summary>
    /// <param name="productLineId">
    /// A <c>ProductSpeciality</c> id. Opaque to the caller: M13 carries it on
    /// <c>Lead.ProductLineId</c> with no foreign key.
    /// </param>
    /// <returns>
    /// <c>null</c> when the line does not exist in that tenant — distinct from a line that exists
    /// and collects nothing, which answers a policy with no role. A caller must be able to tell
    /// "this reference is dangling" from "this product asks for nothing", because the first is a
    /// bug and the second is a current account.
    /// </returns>
    Task<BeneficiaryPolicyDto?> GetBeneficiaryPolicyAsync(
        Guid tenantId, Guid productLineId, CancellationToken ct);

    /// <summary>
    /// Whether a product line may be KYC'd remotely and how such a file is validated (KRM-B-01).
    /// Read by M02 when a converted lead's file is opened, and again when its score comes back.
    ///
    /// <para>
    /// Cached for five minutes over <c>IDistributedCache</c> and invalidated by every write to the
    /// product, like the beneficiary policy next door. A conversion calls this once, so the cache
    /// is about the burst a batch import produces rather than about a hot path.
    /// </para>
    /// </summary>
    /// <param name="productLineId">
    /// A <c>ProductSpeciality</c> id, opaque to the caller. <c>Guid.Empty</c> answers null without
    /// a query: a KYC file opened for a product nobody named has no policy to apply.
    /// </param>
    /// <returns>
    /// <c>null</c> when the line does not exist in that tenant — a DANGLING reference, which is
    /// not the same thing as a line that forbids remote KYC. Both end with no link being sent, but
    /// only the first is a bug, and the agent is told which.
    /// </returns>
    Task<KycRemotePolicyDto?> GetKycRemotePolicyAsync(
        Guid tenantId, Guid productLineId, CancellationToken ct);

    /// <summary>
    /// Resolves a free-text product label to a product line id (BNF-B-01), or <c>null</c> when
    /// nothing matches exactly.
    ///
    /// <para>
    /// Exists because <c>Lead.InterestedProduct</c> is a LABEL — "Crédit commerçant", and
    /// "Unknown" when a source said nothing — while a beneficiary policy can only be reached
    /// through a catalogue id. This is the bridge, and it is best-effort by design: a label that
    /// matches nothing resolves to null, and an agent picks the line during enrichment.
    /// </para>
    ///
    /// <para>
    /// Matching is EXACT on the product's code or its name, with casing and accents neutralised
    /// ("CREDIT COMMERCANT" finds "Crédit commerçant"), over ACTIVE products only. Never fuzzy:
    /// the resolved line decides which fields gate a conversion and which KYC tier is required, so
    /// a near-miss must not silently apply another product's rules.
    /// </para>
    /// </summary>
    Task<Guid?> ResolveProductLineAsync(Guid tenantId, string? label, CancellationToken ct);
}

/// <summary>
/// A product line's beneficiary policy as other modules see it (BNF-B-06). Enum-typed concepts are
/// strings so no consumer references the Administration domain assembly.
/// </summary>
/// <param name="AllowMultipleHoldings">
/// Whether a customer may hold this line more than once (BNF-B-22). Carried here rather than left
/// on the product summary so a caller gating a second subscription makes one call, not two — it is
/// a column on the entity, not part of the stored policy.
/// </param>
/// <param name="RequiredFieldsBeforeConversion">Stable field codes, not labels.</param>
/// <param name="RequiredKycTier">
/// "Simplified" | "Full", "None" (no KYC at all — Standard lines only), or null (the tenant's own
/// baseline, which still means a KYC file). Read it through <see cref="KycRequirement"/>.
/// </param>
/// <param name="IntegrationSystem">An <c>IntegrationKind</c> name, or null when the line is CRM-only.</param>
public sealed record BeneficiaryPolicyDto(
    Guid ProductLineId,
    string ProductCode,
    string Category,
    bool IsActive,
    bool AllowMultipleHoldings,
    IReadOnlyList<DesignationRoleDto> AllowedRoles,
    int? MaxChildren,
    int? ChildMaxAge,
    IReadOnlyList<string> RequiredFieldsBeforeConversion,
    string? RequiredKycTier,
    string? IntegrationSystem);

/// <summary>
/// What a line's <see cref="BeneficiaryPolicyDto.RequiredKycTier"/> means for its customer.
/// </summary>
public static class KycRequirement
{
    /// <summary>The tier of a line that requires no KYC. Only a Standard line may carry it.</summary>
    public const string None = "None";

    /// <summary>
    /// Whether a customer of this line must have a KYC file. True for null — an unconfigured line
    /// keeps the tenant's baseline, which is a file — and for an unknown line: a gate that cannot
    /// tell must not waive the check.
    /// </summary>
    public static bool IsRequired(string? requiredKycTier)
        => !string.Equals(requiredKycTier, None, StringComparison.OrdinalIgnoreCase);

    /// <summary>The tier to compare a file against, or null when any approved file will do.</summary>
    public static string? Tier(string? requiredKycTier)
        => IsRequired(requiredKycTier) ? requiredKycTier : null;
}

/// <summary>
/// A product line's remote-KYC policy as other modules see it (KRM-B-01). Enum-typed concepts are
/// strings so no consumer references the Administration domain assembly — and here they already
/// ARE strings in the domain, because they name M02's own enums.
/// </summary>
/// <param name="ProductName">
/// What an agent reads. Carried so a notification can name the product without a second call into
/// this module — a mail telling an agent "a remote file is waiting" is useless without it.
/// </param>
/// <param name="IsActive">
/// <c>false</c> once the line has been retired. Carried rather than inferred, because a retired
/// line keeps its policy and M02 must be able to say "this product is no longer sold" instead of
/// "this product forbids remote KYC".
/// </param>
/// <param name="Enabled">Whether a conversion on this line sends a remote-collection link.</param>
/// <param name="ValidationMode">"Manual" | "Automatic".</param>
/// <param name="RequiredTier">"Simplified" | "Full", or null when the line states no level.</param>
/// <param name="MinAutoScore">Score floor for an automatic approval, 50..100.</param>
/// <param name="RequiredDocuments"><c>KycDocumentKind</c> names the customer must send.</param>
/// <param name="AllowedDocumentTypes"><c>KycDocumentType</c> names accepted as identity document.</param>
/// <param name="LinkValidityHours">Lifetime of the link, in hours.</param>
public sealed record KycRemotePolicyDto(
    Guid ProductLineId,
    string ProductCode,
    string ProductName,
    string Category,
    bool IsActive,
    bool Enabled,
    string ValidationMode,
    string? RequiredTier,
    int MinAutoScore,
    IReadOnlyList<string> RequiredDocuments,
    IReadOnlyList<string> AllowedDocumentTypes,
    int LinkValidityHours);

/// <param name="SharesMustTotal100">
/// Whether the quote-parts of this role must sum to 100% within a rank (BNF-B-15).
/// </param>
public sealed record DesignationRoleDto(
    string Code,
    string Label,
    int MinCount,
    int? MaxCount,
    bool SharesMustTotal100);

/// <summary>
/// The compliance parameters of one tenant, as other modules see them. Enum-typed domain concepts
/// are exposed as strings so no consumer has to reference the Administration domain assembly.
/// </summary>
/// <param name="FatcaEnabled">
/// When false every other FATCA member is null or meaningless, and no FATCA field may be required,
/// exposed, or counted towards a lead's completeness.
/// </param>
/// <param name="FatcaRegime">"IgaModel1" | "IgaModel2" | "DirectFfiAgreement", or null when disabled.</param>
/// <param name="FatcaGiin">
/// The institution's GIIN, <c>XXXXXX.XXXXX.XX.XXX</c>, or null when disabled. Well-formed by
/// construction: enabling FATCA without one is refused.
/// </param>
/// <param name="FatcaRequiredAt">"Conversion" | "Enrichment".</param>
/// <param name="FatcaForCoHolders">Whether indicia are collected from co-holders as well.</param>
/// <param name="PepPostMandateMonths">
/// Months a person stays politically exposed after their function ends. Feed it to
/// <c>PepStatus.IsExposedAt</c> rather than comparing dates by hand — that method is the single
/// definition of the window.
/// </param>
public sealed record ComplianceSettingsDto(
    bool FatcaEnabled,
    string? FatcaRegime,
    string? FatcaGiin,
    string FatcaRequiredAt,
    bool FatcaForCoHolders,
    /// <summary>
    /// Whether PEP vigilance is active for this tenant. Carried across the module boundary because
    /// M13 stops requiring a PEP declaration when it is off; TRUE for a tenant with no row.
    /// </summary>
    bool PepEnabled,
    int PepPostMandateMonths,
    /// <summary>
    /// Whether this institution allows a remote KYC file to be approved by the machine at all
    /// (KRM-B-01). FALSE for a tenant with no row, and false is also what an outage answers: the
    /// decision to remove the human from a KYC circuit is never taken by default.
    /// </summary>
    bool KycAutoApprovalEnabled = false,
    /// <summary>
    /// Highest tier the machine may grant — "Simplified" or "Full". "Simplified" for a tenant with
    /// no row, which bounds a wrong machine decision by the simplified ceilings.
    /// </summary>
    string KycAutoApprovalMaxTier = "Simplified",
    /// <summary>Share of automatic approvals drawn for an after-the-fact review (KRM-B-11), 0..100.</summary>
    int KycAutoApprovalSamplePct = 10);

/// <summary>
/// Read-only projection of tenant email provider settings exposed to
/// the Notifications module. Credentials are never included — only the
/// vault reference path.
/// </summary>
/// <summary>
/// A tenant's effective email configuration — everything EXCEPT the credential itself, which
/// the consumer fetches from the vault with <see cref="NotificationSecrets.CredentialKey"/>.
/// Keeping the secret out of this DTO is what lets M08 cache the rest in Redis.
/// </summary>
public sealed record TenantNotificationConfigDto(
    string ProviderType,
    bool UseDefaultPlatformProvider,
    string? FromEmail,
    string? FromName,
    string? ReplyToEmail,
    string? SendingDomain,
    int? MonthlyQuotaLimit,
    /// <summary>Whether a credential was stored for this provider. Never the credential.</summary>
    bool HasCredential,
    // ── SMTP relay settings, meaningful when ProviderType is "Smtp" ─────────
    string? SmtpHost,
    int? SmtpPort,
    string? SmtpUsername,
    bool SmtpUseSsl,
    bool SmtpUseStartTls);

/// <summary>
/// Read-only projection of an agency, safe to hand to other modules: identifier,
/// business code, display name, place in the hierarchy and activity flag — nothing
/// that would let a consumer bypass the Administration module's own rules.
/// </summary>
public sealed record AgencySummary(
    Guid Id,
    string Code,
    string Name,
    Guid? ParentAgencyId,
    bool IsActive);

/// <summary>
/// Read-only projection of an agent, safe to hand to other modules.
/// Deliberately NOT the same type as the Users module's internal User
/// entity — this decouples Leads from any future change to that entity.
/// </summary>
/// <summary>
/// One CRM account matched by its address (TEL-B-05). <paramref name="Email"/> comes back as the
/// column holds it, not as the caller spelled it, so a screen shows the account's own address.
/// </summary>
public sealed record UserSupervisors(Guid? LineManagerId, Guid? AgencyManagerId)
{
    public static readonly UserSupervisors None = new(null, null);
}

public sealed record UserEmailMatch(
    Guid UserId,
    string Email,
    string FullName,
    Guid? AgencyId,
    bool IsActive);

public sealed record AgentSummary(
    Guid Id,
    string FullName,
    Guid AgencyId,
    IReadOnlyList<string> SpokenLanguages,
    IReadOnlyList<string> Specialties,
    GeoPoint? CurrentLocation,
    int ActiveLeadsCount,
    int HotLeadsCount,
    double ConversionRate30d,
    bool IsAvailable);

/// <param name="Limit">Null when the tenant has no monthly limit.</param>
public sealed record EmailQuotaDecision(bool Granted, int? Limit, int UsedThisMonth);

/// <param name="Limit">Null when the tenant has no monthly limit.</param>
/// <param name="Reason">
/// Why a refusal was a refusal — <c>CHANNEL_NOT_CONFIGURED</c> or <c>MONTHLY_QUOTA_EXCEEDED</c>.
/// The email decision has no equivalent because an email always has a platform account to fall
/// back on; here the two refusals call for different actions (configure a provider, or raise a
/// ceiling) and a dead-lettered message has to say which.
/// </param>
public sealed record SmsQuotaDecision(
    bool Granted, int? Limit, int UsedThisMonth, string? Reason = null);

/// <summary>
/// A tenant's SMS channel as other modules see it: which provider, under which sender identity,
/// and whether it can actually send. Never a credential.
/// </summary>
/// <param name="ProviderType"><c>Disabled</c>, <c>Brevo</c> or <c>Twilio</c>.</param>
/// <param name="SenderId">
/// Brevo's alphanumeric sender ("SANKORE", ≤11 characters) or Twilio's own number / messaging
/// service SID. One field, because what it means is the provider's business.
/// </param>
/// <param name="AccountSid">Twilio's Account SID. Null for Brevo.</param>
/// <param name="HasCredential">A secret is stored in the vault. Not the secret.</param>
/// <param name="CanSend">
/// The only question most callers have: provider chosen, sender identity set, credential stored
/// and — for Twilio — an Account SID present. Computed by the entity so no caller re-derives it
/// and gets it subtly wrong.
/// </param>
/// <param name="DefaultCallingCode">
/// The country calling code, digits only, that turns a locally-stored telephone number into an
/// E.164 address. Configured per tenant because nothing else in this platform knows it — see the
/// entity for why it is not inferred.
/// </param>
public sealed record TenantSmsConfigDto(
    string ProviderType,
    string? SenderId,
    string? AccountSid,
    string? DefaultCallingCode,
    bool HasCredential,
    bool CanSend,
    int? MonthlyQuotaLimit,
    int CurrentMonthUsageCount);

/// <summary>
/// Read-only projection of one product of the tenant's catalogue, safe to hand to other modules.
///
/// <para>
/// Four fields and deliberately not the whole entity: <c>ParametersJson</c> is a category-dependent
/// free-form payload whose schema the owning module validates, and the CBS link
/// (<c>BusinessProductId</c>) belongs to M12's own integration screen. A consumer needs the
/// identity (<paramref name="Code"/>), something to show (<paramref name="Name"/>), the axis it
/// reasons on (<paramref name="Category"/>) and whether the institution still sells it
/// (<paramref name="IsActive"/>).
/// </para>
/// </summary>
/// <param name="Code">Upper-cased, unique per tenant. The value other modules store.</param>
/// <param name="Category">A <see cref="ProductCategory"/> name.</param>
/// <param name="IsActive">
/// <c>false</c> once the product has been retired from the catalogue. A retirement never deletes
/// the row, so a code other modules hold keeps resolving — it resolves to an inactive product.
/// </param>
/// <param name="Id">
/// The product line id — what <c>Lead.ProductLineId</c> stores and what
/// <see cref="IAdministrationModule.GetBeneficiaryPolicyAsync"/> takes. Appended with a default so
/// the existing positional call sites keep compiling; the facade always fills it.
/// </param>
/// <param name="AllowMultipleHoldings">Whether a customer may hold this line more than once (BNF-B-22).</param>
public sealed record ProductSummary(
    string Code,
    string Name,
    string Category,
    bool IsActive,
    Guid Id = default,
    bool AllowMultipleHoldings = true);

/// <summary>
/// A templated notification addressed to one of the tenant's users (see
/// <see cref="IAdministrationModule.NotifyUserAsync"/>).
/// </summary>
/// <param name="TemplateKey">
/// Key of a seeded email template. An unknown key is a failed send, not an exception.
/// </param>
/// <param name="IdempotencyKey">
/// Deduplicates the message in M08. Key it on what the notification is ABOUT — the removed
/// household member, the assigned task — rather than on the broker's message id, so a replayed
/// event cannot produce a second mail even if an inbox table was recreated.
/// </param>
/// <param name="TemplateData">
/// Values the template needs. <c>full_name</c> and <c>company_name</c> are added by this module.
/// Must carry NO sensitive value: a rendered body travels to a mail provider and sits in the
/// delivery log.
/// </param>
public sealed record NotifyUserRequest(
    Guid TenantId,
    Guid UserId,
    string TemplateKey,
    string IdempotencyKey,
    IReadOnlyDictionary<string, object>? TemplateData = null);

/// <param name="Status">Broker, GeneralAgent or BancassurancePartner; null when not configured.</param>
/// <param name="MissingDocuments">Required kinds with no current piece (Approval, FinancialGuarantee, ProfessionalLiability).</param>
/// <param name="ExpiredDocuments">Required kinds whose current piece has expired.</param>
public sealed record InsuranceDistributorStanding(
    bool IsConfigured,
    string? Status,
    string? RegistrationNumber,
    string? LegalMention,
    decimal? AnnualSmig,
    IReadOnlyList<string> MissingDocuments,
    IReadOnlyList<string> ExpiredDocuments)
{
    public bool InGoodStanding => IsConfigured && MissingDocuments.Count == 0 && ExpiredDocuments.Count == 0;

    public static InsuranceDistributorStanding NotConfigured { get; } = new(false, null, null, null, null, [], []);
}

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
    /// Le nom affiché de chaque utilisateur demandé, en un appel — pour une liste qui montre un
    /// conseiller par ligne. Un identifiant inconnu (ou d'un autre tenant) est absent du résultat.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> userIds, CancellationToken ct);

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
/// <param name="RequiredKycTier">"Simplified" | "Full", or null.</param>
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
    int PepPostMandateMonths);

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

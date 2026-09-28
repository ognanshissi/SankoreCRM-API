namespace Sankore.Modules.Customers.Features.Clients.Shared;

/// <summary>
/// One contact point as every read endpoint returns it: the value is MASKED, never
/// clear. The clear value is served only by <c>POST clients/{id}/reveal</c>, which is
/// permission-gated, rate-limited and writes a <c>SensitiveDataAccessLog</c> row.
/// </summary>
public sealed record ClientContactPointDto(
    Guid Id,
    string Type,
    string? ValueMasked,
    string? Label,
    bool IsPrimary,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    bool IsActive);

/// <summary>One line of the client's status trail (who moved it, when and why).</summary>
/// <summary>
/// Trimmed status-history entry embedded in the client detail — the last few transitions,
/// without the row id. Deliberately NOT named <c>ClientStatusHistoryDto</c>: that name belongs
/// to the addressable row returned by <c>GET clients/{id}/status-history</c> in the Lifecycle
/// zone, and two schemas sharing a simple name collide in the OpenAPI document.
/// </summary>
public sealed record ClientStatusHistorySummaryDto(
    string? OldStatus,
    string NewStatus,
    string? Reason,
    Guid ActorUserId,
    DateTimeOffset OccurredAt);

/// <summary>
/// Where a merged record now lives. Returned instead of a bare id so the UI can show
/// the survivor's client number without a second round-trip.
/// </summary>
public sealed record MergedIntoDto(Guid ClientId, string ClientNumber);

/// <summary>
/// Full client record for the detail screen. Names, professions and places are clear
/// text (they are not protected fields); every protected field appears ONLY in its
/// <c>...Masked</c> form.
///
/// <see cref="Version"/> is PostgreSQL's <c>xmin</c>: the caller echoes it back as
/// <c>ExpectedVersion</c> on the next PATCH, which is how a lost update turns into a
/// <c>CONCURRENCY_CONFLICT</c> instead of silently overwriting a colleague's edit.
/// </summary>
public sealed record ClientDetailDto(
    Guid Id,
    string ClientNumber,
    string ClientType,
    string Status,
    string DisplayName,

    // ── Individual identity (clear: not protected data) ─────────────────────
    string? FirstName,
    string? LastName,
    string? MaidenName,
    string? Gender,
    string? BirthPlace,
    string? Nationality,
    string? MaritalStatus,
    string? FatherName,
    string? MotherName,
    string? Profession,
    string? Employer,
    string PreferredLanguage,
    int DependentsCount,

    // ── Protected fields: masked renderings only ────────────────────────────
    string? DateOfBirthMasked,
    string? DeclaredIncomeMasked,
    string? DeclaredIncomeCurrency,
    string? IdentityDocumentType,
    string? IdentityDocumentNumberMasked,
    DateOnly? IdentityDocumentIssuedOn,
    DateOnly? IdentityDocumentExpiresOn,

    // ── Legal entity identity ───────────────────────────────────────────────
    string? LegalName,
    string? LegalFormCode,
    string? RegistrationNumberMasked,
    string? TaxIdNumberMasked,
    DateOnly? IncorporationDate,

    // ── Routing, KYC, commercial ────────────────────────────────────────────
    Guid AgencyId,
    string AgencyCode,
    Guid? AdvisorUserId,
    string KycStatus,
    string? KycRejectionReason,
    string RiskLevel,
    string? SegmentCode,
    int? LoyaltyScore,
    bool LoyaltyScoreProvisional,

    // ── Lifecycle ───────────────────────────────────────────────────────────
    Guid? SourceLeadId,
    MergedIntoDto? MergedInto,
    bool IsAnonymized,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version,

    IReadOnlyList<ClientContactPointDto> ContactPoints,
    IReadOnlyList<ClientStatusHistorySummaryDto> StatusHistory);

/// <summary>
/// One row of the client search result. Kept deliberately narrow so the list query
/// stays a single projected SELECT: the only protected value here is the primary
/// phone, and it is masked.
/// </summary>
public sealed record ClientSearchItemDto(
    Guid Id,
    string ClientNumber,
    string DisplayName,
    string ClientType,
    string Status,
    Guid AgencyId,
    Guid? AdvisorUserId,
    string KycStatus,
    string RiskLevel,
    string? SegmentCode,
    string? PrimaryPhoneMasked);

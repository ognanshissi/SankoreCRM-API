namespace Sankore.Modules.Leads.Features.CaptureLead;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

public sealed record CaptureLeadCommand(
    Guid TenantId,
    string FullName,
    string PhoneNumber,
    LeadSource Source,
    string InterestedProduct,
    string PreferredLanguage,
    double Latitude,
    double Longitude,
    Guid? PreferredAgencyId,
    string? FirstName = null,
    string? LastName = null,
    string? Email = null,
    LeadGender Gender = LeadGender.Unknown,
    DateOnly? DateOfBirth = null,
    decimal? DesiredAmount = null,
    string? DesiredCurrency = null,
    string? Campaign = null,
    LeadChannel? Channel = null,
    string? Comment = null,
    string? ExternalReference = null,
    Guid? OwnerId = null,
    Guid? AgencyId = null,
    Guid? AgentCollectedLeadId = null,
    string? CompanyName = null,
    string? CompanyEmail = null,
    string? CompanyPhone = null,
    string? Website = null,
    LeadType ProspectType = LeadType.Individual,
    string? NationalId = null,
    string? CustomerReference = null,
    /// <summary>
    /// Controls whether a duplicate match blocks creation (409) or just warns (201 + DuplicateDetected=true).
    /// </summary>
    DuplicateGateMode GateMode = DuplicateGateMode.Block,
    /// <summary>
    /// Minimum confidence score (0-100) for a candidate to be considered a potential duplicate.
    /// Defaults to <see cref="IdentityMatchScorer.MinConfidence"/> (30).
    /// </summary>
    double MinConfidenceThreshold = IdentityMatchScorer.MinConfidence,
    /// <summary>
    /// When true, bypasses the duplicate gate entirely and forces creation regardless of matches.
    /// </summary>
    bool Force = false,
    /// <summary>
    /// The configured source this lead arrived through, or null when there is none (manual UI
    /// capture, file import).
    ///
    /// SERVER-SET ONLY — deliberately absent from <c>CaptureLeadRequest</c> and
    /// <c>SystemCaptureLeadRequest</c>. Nothing validates this id, so a client able to set it
    /// could name any source in the tenant and thereby inherit its
    /// <c>DefaultDispatchingRuleId</c> — choosing which rule, and so which agent pool, routes
    /// its leads past the administrator's configuration — and distort ROI reporting, which
    /// prices a source at CostPerLead × leads received. Every caller that legitimately knows an
    /// id is server-side: PullLeadSourceJob, ReplayIngestionJob, and the ingest handler which
    /// sets it from an already tenant-checked config.
    /// </summary>
    Guid? LeadSourceConfigId = null,
    /// <summary>
    /// The M01 client this request belongs to, when the caller already knows (BNF-B-22) — a
    /// counter clerk opening a second product for the client in front of them. When null, the
    /// handler asks M01 and attaches only on a unique, person-identifying match.
    ///
    /// SERVER-SET ONLY, like <see cref="LeadSourceConfigId"/> above and for a comparable reason:
    /// an attachment decides which client record the whole request reads its identity, household,
    /// PEP and FATCA from (BNF-B-23). An unauthenticated ingest able to set it would point a lead
    /// at an arbitrary client of the tenant. The authenticated capture endpoint may carry it; the
    /// public ones may not.
    /// </summary>
    Guid? CustomerId = null,
    /// <summary>
    /// The M12 product line, when the caller resolved it. Server-set only — see
    /// <c>Lead.ProductLineId</c> for why a caller able to choose it would be choosing the
    /// beneficiary policy that gates its own conversion.
    /// </summary>
    Guid? ProductLineId = null,
    /// <summary>
    /// The prospect's household, when the capture collected one (BNF-B-18). Created in the SAME
    /// transaction as the lead: a household recorded by a second request is one that can fail on
    /// its own, leaving a lead whose family somebody has to remember to re-enter.
    /// </summary>
    IReadOnlyList<CaptureHouseholdMember>? Household = null,
    /// <summary>
    /// Questionnaire answers collected at capture (BNF-B-18). A health one arrives with its
    /// consent proof — the endpoint refuses the request otherwise, before anything is written.
    /// </summary>
    IReadOnlyList<CaptureQuestionnaire>? Questionnaires = null,
    /// <summary>
    /// Values of the tenant's custom lead fields, keyed by field key — checked against the lead
    /// form before anything is written. Absent on every path that has never heard of them.
    /// </summary>
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? CustomFields = null,
    /// <summary>
    /// SERVER-SET: true on the interactive capture only. Enforces the fields the tenant made
    /// required; an import or an ingest would otherwise lose every prospect a new form setting does
    /// not describe.
    /// </summary>
    bool EnforceFormRequirements = false
) : IRequest<Result<CaptureLeadResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => null;
}

/// <param name="LeadId">Id of the created lead. Null only when <see cref="DuplicateDetected"/> is true AND <see cref="DuplicateGateMode.Block"/> was used.</param>
/// <param name="DuplicateDetected">
/// True when potential duplicates were found.
/// In <see cref="DuplicateGateMode.Block"/> mode the lead was NOT created (LeadId is null) — re-submit with Force=true to override.
/// In <see cref="DuplicateGateMode.Warn"/> mode the lead WAS created (LeadId is set) — surface the warning to the user.
/// </param>
/// <param name="LinkedCustomerId">
/// Set when the lead was attached to an existing client at capture (BNF-B-22) — either because the
/// caller named one, or because M01 returned a single person-identifying match. Null when nothing
/// matched AND when something did but not confidently enough to act on: in the second case the
/// candidates are available from <c>GET leads/{id}/customer-matches</c>, which is the only route
/// that leads to an attachment, because BNF-B-22 forbids a silent one.
/// </param>
public sealed record CaptureLeadResult(
    Guid? LeadId,
    string? Status,
    bool DuplicateDetected = false,
    IReadOnlyList<DuplicateMatchResult>? PotentialDuplicates = null,
    Guid? LinkedCustomerId = null);

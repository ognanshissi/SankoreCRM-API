namespace Sankore.Modules.Leads.Features.GetLead;

using MediatR;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Sankore.Modules.Leads.Features.NextAction;

internal sealed record GetLeadQuery(Guid LeadId) : ILeadReadRequest, IRequest<Result<LeadDto>>;

public sealed record LeadDto(
    Guid Id,
    Guid TenantId,
    string FullName,
    string? FirstName,
    string? LastName,
    string PhoneNumber,
    string? Email,
    string Gender,
    DateOnly? DateOfBirth,
    string? CompanyName,
    string? CompanyEmail,
    string? CompanyPhone,
    string? Website,
    string Status,
    string PipelineStage,
    string Source,
    string? Channel,
    string? Campaign,
    string? ExternalReference,
    string? Comment,
    DateTimeOffset CapturedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt,
    string InterestedProduct,
    Money? DesiredAmount,
    string PreferredLanguage,
    double QualificationCompleteness,
    int Score,
    string IntentLevel,
    double? Latitude,
    double? Longitude,
    Guid? PreferredAgencyId,
    Guid? OwnerId,
    Guid? AgencyId,
    Guid? CurrentAssignedId,
    Guid? CurrentAssignmentId,
    DateTimeOffset? LastActivityAt,
    string? LossReason,
    DateTimeOffset? ConvertedAt,
    Guid? ConvertedToCustomerId,
    string? NationalId,
    string? CustomerReference,
    string ProspectType,
    /// <param name="LeadSourceConfigId">
    /// The configured source the lead arrived through, null when there is none (typed into the
    /// UI, imported from a file, produced by a merge). Distinct from <c>Source</c>, which is a
    /// coarse enum and cannot name a specific configured form or webhook.
    /// </param>
    Guid? LeadSourceConfigId = null,
    /// <param name="LeadSourceCode">
    /// The source's <c>Code</c>, resolved for display. Null when the lead carries no source, and
    /// also when the id no longer resolves — the reference is opaque with no foreign key, so a
    /// source can be archived out from under it.
    /// </param>
    string? LeadSourceCode = null,
    /// <param name="LeadSourceLabel">The source's human-readable <c>Label</c>, same caveats.</param>
    string? LeadSourceLabel = null,
    /// <param name="AwaitingDispatchSince">
    /// When the lead entered the dispatch queue — no agent could take it, and it is retried until
    /// one can. Null when it is not waiting.
    /// </param>
    DateTimeOffset? AwaitingDispatchSince = null,
    /// <param name="CustomFields">
    /// Values of the tenant's custom lead fields, keyed by field key (<c>GET forms/Lead</c> says
    /// how to render them). Filled on the detail read only; null in lists.
    /// </param>
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? CustomFields = null,
    /// <param name="Address">
    /// Where the principal lives — the postal fields of <c>PersonIdentity</c> plus the quartier and
    /// the point de repère, which only leads carry. Filled on the detail read only; null in lists,
    /// because the address line and the landmark are decrypted per row.
    /// </param>
    LeadAddressDto? Address = null,
    /// <param name="UsageName">The principal's nom d'usage. Detail read only.</param>
    string? UsageName = null,
    /// <param name="NameVerifiedAt">When an agent confirmed the spelling of the name; null until then.</param>
    DateTimeOffset? NameVerifiedAt = null,
    /// <param name="IdentitySourceActivityId">
    /// The call or visit that confirmed the identity, null while it is only what the prospect
    /// declared at capture.
    /// </param>
    Guid? IdentitySourceActivityId = null,
    /// <param name="QualificationThreshold">
    /// The score at or above which qualifying marks the lead <c>Qualified</c>
    /// (<c>Leads:QualificationThreshold</c>), so the screen can draw it next to the score.
    /// </param>
    int? QualificationThreshold = null,
    /// <param name="NextStep">
    /// The one thing to do next and when (<see cref="LeadNextSteps"/>). Null on a closed lead.
    /// Filled on the detail read AND in lists — it is what a list of leads is scanned for.
    /// </param>
    LeadNextStepDto? NextStep = null,
    /// <param name="Sla">
    /// The first-contact SLA of the current assignment. Null when the lead has no agent. Filled on
    /// the detail read AND in lists — the lead board flags late and soon-late leads from it.
    /// </param>
    LeadSlaDto? Sla = null);

/// <summary>The principal's address, as the identity screen edits it.</summary>
/// <param name="PostalCode">Also the boîte postale — the only postal code most of the network has.</param>
/// <param name="CountryCode">ISO 3166-1 alpha-2.</param>
public sealed record LeadAddressDto(
    string? CountryCode,
    string? City,
    string? Neighbourhood,
    string? Landmark,
    string? AddressLine,
    string? PostalCode);

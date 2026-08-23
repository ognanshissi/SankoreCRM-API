namespace Sankore.Modules.Leads.Features.CaptureLead;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Command: capture a new prospect (F13.1). This is the entry point of the
/// whole lead funnel — a lead is Captured, then Qualified (separate slice,
/// not shown here), then Dispatched (see Features/DispatchLead).
/// </summary>
public sealed record CaptureLeadCommand(
    Guid TenantId,
    string FullName,
    string PhoneNumber,
    LeadSource Source,
    string InterestedProduct,
    string PreferredLanguage,
    double Latitude,
    double Longitude,
    Guid? PreferredAgencyId
) : IRequest<Result<CaptureLeadResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => null; // ID not yet assigned at dispatch time
}

public sealed record CaptureLeadResult(Guid LeadId, string Status);

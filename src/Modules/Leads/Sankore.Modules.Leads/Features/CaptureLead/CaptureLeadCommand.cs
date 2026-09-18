namespace Sankore.Modules.Leads.Features.CaptureLead;

using MediatR;
using Sankore.Modules.Leads.Domain;
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
    string? Website = null
) : IRequest<Result<CaptureLeadResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => null;
}

public sealed record CaptureLeadResult(Guid LeadId, string Status);

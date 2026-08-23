using MediatR;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.CreateAgency;

public sealed record CreateAgencyCommand(
    string Name,
    string Description,
    AgencyType AgencyType,
    Guid? ParentAgencyId,
    string? AddressStreet,
    string? AddressCity,
    string? AddressState,
    string? AddressCountry,
    string? AddressZipCode,
    double? Latitude,
    double? Longitude
) : IRequest<Result<CreateAgencyResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Agency";
    public string? ResourceId => null; // ID not yet assigned at dispatch time
}

public sealed record CreateAgencyResult(Guid AgencyId, string Code);

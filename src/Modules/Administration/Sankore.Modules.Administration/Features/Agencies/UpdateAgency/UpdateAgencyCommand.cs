using MediatR;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.UpdateAgency;

public sealed record UpdateAgencyCommand(
    Guid AgencyId,
    string Name,
    string Description,
    AgencyType AgencyType,
    string? AddressStreet,
    string? AddressCity,
    string? AddressState,
    string? AddressCountry,
    string? AddressZipCode,
    double? Latitude,
    double? Longitude
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Agency";
    public string? ResourceId => AgencyId.ToString();
}

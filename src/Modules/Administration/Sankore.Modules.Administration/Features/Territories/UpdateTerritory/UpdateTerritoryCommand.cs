using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Territories.UpdateTerritory;

public sealed record UpdateTerritoryCommand(
    Guid TerritoryId,
    string Name,
    string Description,
    double? Latitude,
    double? Longitude,
    double RayonKm,
    List<string> ProductSpecialities
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Territory";
    public string? ResourceId => TerritoryId.ToString();
}

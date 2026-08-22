using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Territories.CreateTerritory;

public sealed record CreateTerritoryCommand(
    string Name,
    string Code,
    string Description,
    double? Latitude,
    double? Longitude,
    double RayonKm,
    List<string> ProductSpecialities
) : IRequest<Result<CreateTerritoryResult>>, ICommand;

public sealed record CreateTerritoryResult(Guid TerritoryId);
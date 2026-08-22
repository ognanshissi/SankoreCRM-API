using MediatR;
using Sankore.Modules.Administration.Features.Territories;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Territories.GetTerritory;

public sealed record GetTerritoryQuery(Guid TerritoryId) : IRequest<Result<TerritoryDto>>;

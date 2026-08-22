using MediatR;
using Sankore.Modules.Administration.Features.Territories;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Territories.ListTerritories;

/// <summary>Returns all active territories for the current tenant.</summary>
public sealed record ListTerritoriesQuery(bool IncludeInactive = false)
    : IRequest<Result<List<TerritoryDto>>>;

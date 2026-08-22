using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Territories.DeleteTerritory;

/// <summary>Soft-deletes a territory by setting IsActive = false.</summary>
public sealed record DeleteTerritoryCommand(Guid TerritoryId) : IRequest<Result>, ICommand;

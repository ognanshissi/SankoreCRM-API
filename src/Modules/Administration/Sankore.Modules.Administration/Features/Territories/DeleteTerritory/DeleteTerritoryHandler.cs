using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Territories.DeleteTerritory;

internal sealed class DeleteTerritoryHandler(
    AdministrationDbContext db
) : IRequestHandler<DeleteTerritoryCommand, Result>
{
    public async Task<Result> Handle(DeleteTerritoryCommand request, CancellationToken ct)
    {
        var territory = await db.Territories
            .AsTracking()
            .Where(t => t.Id == request.TerritoryId)
            .SingleOrDefaultAsync(ct);

        if (territory is null)
            return Result.Fail($"Territory {request.TerritoryId} not found.");

        if (!territory.IsActive)
            return Result.Fail("Territory is already deactivated.");

        territory.Deactivate();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

namespace Sankore.Modules.Integration.Features.Mappings.DeleteMapping;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

internal sealed class DeleteMappingHandler(IntegrationDbContext db)
    : IRequestHandler<DeleteMappingCommand, Result>
{
    public async Task<Result> Handle(DeleteMappingCommand cmd, CancellationToken ct)
    {
        if (!await db.ConnectionExistsAsync(cmd.ConnectionId, ct))
            return Result.Fail(IntegrationErrors.ConnectionNotFound);

        var crmCode = cmd.CrmCode.Trim();

        var mapping = await db.Mappings
            .AsTracking()
            .FirstOrDefaultAsync(
                m => m.ConnectionId == cmd.ConnectionId
                     && m.Domain == cmd.Domain
                     && m.CrmCode == crmCode,
                ct);

        // The tenant query filter is what makes another tenant's row invisible here, so this
        // answers "not found" for it too — never "forbidden".
        if (mapping is null)
            return Result.Fail(IntegrationErrors.MappingNotFound);

        db.Mappings.Remove(mapping);
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}

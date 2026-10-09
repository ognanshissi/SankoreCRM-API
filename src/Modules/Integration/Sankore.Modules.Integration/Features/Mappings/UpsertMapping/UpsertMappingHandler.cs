namespace Sankore.Modules.Integration.Features.Mappings.UpsertMapping;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class UpsertMappingHandler(
    IntegrationDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser,
    TimeProvider clock)
    : IRequestHandler<UpsertMappingCommand, Result<UpsertMappingResponse>>
{
    public async Task<Result<UpsertMappingResponse>> Handle(
        UpsertMappingCommand cmd, CancellationToken ct)
    {
        if (!await db.ConnectionExistsAsync(cmd.ConnectionId, ct))
            return Result.Fail<UpsertMappingResponse>(IntegrationErrors.ConnectionNotFound);

        var crmCode = cmd.CrmCode.Trim();

        // AsTracking: the context is NoTracking by default and this one is here to be mutated.
        var existing = await db.Mappings
            .AsTracking()
            .FirstOrDefaultAsync(
                m => m.ConnectionId == cmd.ConnectionId
                     && m.Domain == cmd.Domain
                     && m.CrmCode == crmCode,
                ct);

        var created = existing is null;

        if (existing is null)
        {
            existing = IntegrationMapping.Create(
                tenant.CurrentTenantId,
                cmd.ConnectionId,
                cmd.Domain,
                crmCode,
                cmd.ExternalCode,
                currentUser.Id,
                clock,
                cmd.Label);

            db.Mappings.Add(existing);
        }
        else
        {
            existing.Update(cmd.ExternalCode, cmd.Label, currentUser.Id, clock);
        }

        await db.SaveChangesAsync(ct);

        return Result.Ok(new UpsertMappingResponse(created, existing.ToDto()));
    }
}

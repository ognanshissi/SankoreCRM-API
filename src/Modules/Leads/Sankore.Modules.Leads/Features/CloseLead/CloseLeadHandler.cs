namespace Sankore.Modules.Leads.Features.CloseLead;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.Tasks;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

internal sealed class CloseLeadHandler(
    LeadsDbContext db,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher? publisher = null,
    AgentCapacityService? capacity = null)
    : IRequestHandler<CloseLeadCommand, Result>
{
    public async Task<Result> Handle(CloseLeadCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads.AsTracking().FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail("LEAD_NOT_FOUND");

        if (lead.IsStale(cmd.ExpectedUpdatedAt))
            return Result.Fail(LeadConcurrency.ConflictError);

        var result = lead.Close(cmd.Reason, cmd.Detail);
        if (result.IsFailure)
            return result;

        var agents = cmd.Reason == LeadCloseReason.Archived
            ? await ArchivedLeadTasks.CancelOpenAsync(db, publisher, [lead.Id], ct)
            : [];

        await db.SaveChangesAsync(ct);

        if (capacity is not null)
            foreach (var (tenantId, agentId) in agents)
                await capacity.InvalidateAsync(tenantId, agentId, ct);
        return Result.Ok();
    }
}

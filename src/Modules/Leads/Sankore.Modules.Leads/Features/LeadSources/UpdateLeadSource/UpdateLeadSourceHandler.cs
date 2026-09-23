namespace Sankore.Modules.Leads.Features.LeadSources.UpdateLeadSource;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Features.LeadSources.Events;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

internal sealed class UpdateLeadSourceHandler(
    LeadsDbContext db,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher)
    : IRequestHandler<UpdateLeadSourceCommand, Result>
{
    public async Task<Result> Handle(UpdateLeadSourceCommand cmd, CancellationToken ct)
    {
        var source = await db.LeadSourceConfigs.AsTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.SourceId, ct);

        if (source is null)
            return Result.Fail("LEAD_SOURCE_NOT_FOUND");

        if (source.Version != cmd.ExpectedVersion)
            return Result.Fail("CONFLICT");

        Money? costPerLead = cmd.CostPerLead.HasValue && cmd.CostCurrency is not null
            ? new Money(cmd.CostPerLead.Value, cmd.CostCurrency)
            : null;

        var changedFields = source.Update(
            label:                cmd.Label,
            description:          cmd.Description,
            displayOrder:         cmd.DisplayOrder,
            settings:             cmd.Settings,
            platformConnectionId: cmd.PlatformConnectionId,
            dedupWindowDays:      cmd.DedupWindowDays,
            costPerLead:          costPerLead);

        await publisher.PublishAsync(new LeadSourceChangedEvent(
            SourceId:      source.Id,
            TenantId:      source.TenantId,
            Code:          source.Code,
            ChangeType:    "Updated",
            ChangedFields: changedFields), ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Fail("CONFLICT");
        }

        return Result.Ok();
    }
}

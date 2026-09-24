namespace Sankore.Modules.Leads.Features.LeadSources.CreateLeadSource;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.Events;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

internal sealed class CreateLeadSourceHandler(
    LeadsDbContext db,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher)
    : IRequestHandler<CreateLeadSourceCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateLeadSourceCommand cmd, CancellationToken ct)
    {
        var codeExists = await db.LeadSourceConfigs
            .AnyAsync(s => s.Code == cmd.Code, ct);

        if (codeExists)
            return Result.Fail<Guid>("LEAD_SOURCE_CODE_ALREADY_EXISTS");

        Money? costPerLead = cmd.CostPerLead.HasValue && cmd.CostCurrency is not null
            ? new Money(cmd.CostPerLead.Value, cmd.CostCurrency)
            : null;

        var source = LeadSourceConfig.Create(
            tenantId:             cmd.TenantId,
            code:                 cmd.Code,
            label:                cmd.Label,
            channelType:          cmd.ChannelType,
            displayOrder:         cmd.DisplayOrder,
            integrationMode:      cmd.IntegrationMode,
            description:          cmd.Description,
            settings:             cmd.Settings,
            platformConnectionId: cmd.PlatformConnectionId,
            dedupWindowDays:      cmd.DedupWindowDays,
            costPerLead:          costPerLead,
            defaultAgencyId:          cmd.DefaultAgencyId,
            defaultDispatchingRuleId: cmd.DefaultDispatchingRuleId);

        db.LeadSourceConfigs.Add(source);

        await publisher.PublishAsync(new LeadSourceChangedEvent(
            SourceId:      source.Id,
            TenantId:      source.TenantId,
            Code:          source.Code,
            ChangeType:    "Created",
            ChangedFields: []), ct);

        await db.SaveChangesAsync(ct);

        return Result.Ok(source.Id);
    }
}

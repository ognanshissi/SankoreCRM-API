namespace Sankore.Modules.Leads.Features.LeadSources.CreateLeadSource;

using MediatR;
using Sankore.Modules.Administration.PublicApi;
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
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher,
    IInstitutionCurrency? institutionCurrency = null,
    ITenantStore? tenants = null)
    : IRequestHandler<CreateLeadSourceCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateLeadSourceCommand cmd, CancellationToken ct)
    {
        var codeExists = await db.LeadSourceConfigs
            .AnyAsync(s => s.Code == cmd.Code, ct);

        if (codeExists)
            return Result.Fail<Guid>("LEAD_SOURCE_CODE_ALREADY_EXISTS");

        // In the institution's currency. Before, a cost sent without a currency was dropped silently.
        Money? costPerLead = null;
        if (cmd.CostPerLead is { } cost)
        {
            var currency = await institutionCurrency.ResolveAsync(cmd.TenantId, cmd.CostCurrency, stored: null, ct);
            if (currency.IsFailure) return Result.Fail<Guid>(currency.Error!);
            costPerLead = new Money(cost, currency.Value);
        }

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
            defaultDispatchingRuleId: cmd.DefaultDispatchingRuleId,
            testKeys:                 await TestModeKeys.IsTestAsync(tenants, cmd.TenantId, ct),
            scorePoints:              cmd.ScorePoints);

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

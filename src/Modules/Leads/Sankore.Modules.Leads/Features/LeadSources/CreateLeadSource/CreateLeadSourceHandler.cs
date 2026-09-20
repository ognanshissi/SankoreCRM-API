namespace Sankore.Modules.Leads.Features.LeadSources.CreateLeadSource;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class CreateLeadSourceHandler(LeadsDbContext db)
    : IRequestHandler<CreateLeadSourceCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateLeadSourceCommand cmd, CancellationToken ct)
    {
        var codeExists = await db.LeadSourceConfigs
            .AnyAsync(s => s.Code == cmd.Code, ct);

        if (codeExists)
            return Result.Fail<Guid>("LEAD_SOURCE_CODE_ALREADY_EXISTS");

        var source = LeadSourceConfig.Create(
            tenantId:     cmd.TenantId,
            code:         cmd.Code,
            label:        cmd.Label,
            displayOrder: cmd.DisplayOrder,
            description:  cmd.Description);

        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync(ct);

        return Result.Ok(source.Id);
    }
}

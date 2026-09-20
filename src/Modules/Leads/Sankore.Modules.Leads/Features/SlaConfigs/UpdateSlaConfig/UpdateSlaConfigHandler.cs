namespace Sankore.Modules.Leads.Features.SlaConfigs.UpdateSlaConfig;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class UpdateSlaConfigHandler(LeadsDbContext db)
    : IRequestHandler<UpdateSlaConfigCommand, Result>
{
    public async Task<Result> Handle(
        UpdateSlaConfigCommand cmd, CancellationToken ct)
    {
        var config = await db.SlaConfigs
            .AsTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.SlaConfigId, ct);

        if (config is null)
            return Result.Fail("SLA_CONFIG_NOT_FOUND");

        config.Update(
            name:                  cmd.Name,
            firstContactDeadline:  cmd.FirstContactDeadline,
            qualificationDeadline: cmd.QualificationDeadline,
            followUpDeadline:      cmd.FollowUpDeadline,
            escalationDeadline:    cmd.EscalationDeadline);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

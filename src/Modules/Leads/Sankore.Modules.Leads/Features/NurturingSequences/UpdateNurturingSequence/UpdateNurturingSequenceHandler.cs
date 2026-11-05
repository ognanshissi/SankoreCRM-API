namespace Sankore.Modules.Leads.Features.NurturingSequences.UpdateNurturingSequence;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;

internal sealed class UpdateNurturingSequenceHandler(LeadsDbContext db, INotificationsModule notifications)
    : IRequestHandler<UpdateNurturingSequenceCommand, Result>
{
    public async Task<Result> Handle(UpdateNurturingSequenceCommand cmd, CancellationToken ct)
    {
        var sequence = await db.NurturingSequences.AsTracking()
            .Include(s => s.Steps)
            .FirstOrDefaultAsync(s => s.Id == cmd.SequenceId, ct);

        if (sequence is null)
            return Result.Fail("NURTURING_SEQUENCE_NOT_FOUND");

        if (!await NurturingStepTemplates.AllExistAsync(notifications, sequence.TenantId, cmd.Steps, ct))
            return Result.Fail(NurturingStepTemplates.NotFound);

        sequence.Update(cmd.Name, cmd.Description);

        var steps = sequence.ReplaceSteps(cmd.Steps
            .OrderBy(s => s.Order)
            .Select(s => (s.DelayFromPrevious, s.EmailTemplateKey, s.Subject)));

        // Said explicitly: a step reached through the collection with its id already set would be
        // taken for an existing row, and its UPDATE would touch nothing.
        foreach (var step in steps)
            db.Entry(step).State = EntityState.Added;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

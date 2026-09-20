namespace Sankore.Modules.Leads.Features.NurturingSequences.UpdateNurturingSequence;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class UpdateNurturingSequenceHandler(LeadsDbContext db)
    : IRequestHandler<UpdateNurturingSequenceCommand, Result>
{
    public async Task<Result> Handle(UpdateNurturingSequenceCommand cmd, CancellationToken ct)
    {
        var sequence = await db.NurturingSequences.AsTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.SequenceId, ct);

        if (sequence is null)
            return Result.Fail("NURTURING_SEQUENCE_NOT_FOUND");

        sequence.Update(cmd.Name, cmd.Description);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

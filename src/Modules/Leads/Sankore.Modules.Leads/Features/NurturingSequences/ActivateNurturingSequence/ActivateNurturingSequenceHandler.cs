namespace Sankore.Modules.Leads.Features.NurturingSequences.ActivateNurturingSequence;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ActivateNurturingSequenceHandler(LeadsDbContext db)
    : IRequestHandler<ActivateNurturingSequenceCommand, Result>
{
    public async Task<Result> Handle(ActivateNurturingSequenceCommand cmd, CancellationToken ct)
    {
        var sequence = await db.NurturingSequences.AsTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.SequenceId, ct);

        if (sequence is null)
            return Result.Fail("NURTURING_SEQUENCE_NOT_FOUND");

        sequence.Activate();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

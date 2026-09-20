namespace Sankore.Modules.Leads.Features.NurturingSequences.DeactivateNurturingSequence;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class DeactivateNurturingSequenceHandler(LeadsDbContext db)
    : IRequestHandler<DeactivateNurturingSequenceCommand, Result>
{
    public async Task<Result> Handle(DeactivateNurturingSequenceCommand cmd, CancellationToken ct)
    {
        var sequence = await db.NurturingSequences.AsTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.SequenceId, ct);

        if (sequence is null)
            return Result.Fail("NURTURING_SEQUENCE_NOT_FOUND");

        sequence.Deactivate();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

namespace Sankore.Modules.Leads.Features.NurturingSequences.CreateNurturingSequence;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class CreateNurturingSequenceHandler(LeadsDbContext db)
    : IRequestHandler<CreateNurturingSequenceCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateNurturingSequenceCommand cmd, CancellationToken ct)
    {
        var sequence = NurturingSequence.Create(
            tenantId:    cmd.TenantId,
            name:        cmd.Name,
            description: cmd.Description);

        foreach (var step in cmd.Steps.OrderBy(s => s.Order))
        {
            sequence.AddStep(
                step.Order,
                step.DelayFromPrevious,
                step.EmailTemplateKey,
                step.Subject);
        }

        db.NurturingSequences.Add(sequence);
        await db.SaveChangesAsync(ct);

        return Result.Ok(sequence.Id);
    }
}

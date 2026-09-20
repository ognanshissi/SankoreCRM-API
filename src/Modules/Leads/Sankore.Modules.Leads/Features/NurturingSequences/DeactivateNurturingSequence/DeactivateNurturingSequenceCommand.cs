namespace Sankore.Modules.Leads.Features.NurturingSequences.DeactivateNurturingSequence;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record DeactivateNurturingSequenceCommand(Guid SequenceId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "NurturingSequence";
    public string? ResourceId  => SequenceId.ToString();
}

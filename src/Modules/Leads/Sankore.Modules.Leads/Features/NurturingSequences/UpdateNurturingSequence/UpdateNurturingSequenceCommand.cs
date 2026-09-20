namespace Sankore.Modules.Leads.Features.NurturingSequences.UpdateNurturingSequence;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record UpdateNurturingSequenceCommand(
    Guid SequenceId,
    string Name,
    string? Description
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "NurturingSequence";
    public string? ResourceId  => SequenceId.ToString();
}

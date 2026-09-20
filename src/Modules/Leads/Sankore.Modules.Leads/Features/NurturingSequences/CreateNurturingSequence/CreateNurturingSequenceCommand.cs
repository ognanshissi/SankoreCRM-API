namespace Sankore.Modules.Leads.Features.NurturingSequences.CreateNurturingSequence;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record CreateNurturingSequenceCommand(
    Guid TenantId,
    string Name,
    string? Description,
    IReadOnlyList<StepInput> Steps
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "NurturingSequence";
    public string? ResourceId  => null;
}

public sealed record StepInput(
    int Order,
    TimeSpan DelayFromPrevious,
    string EmailTemplateKey,
    string? Subject = null);

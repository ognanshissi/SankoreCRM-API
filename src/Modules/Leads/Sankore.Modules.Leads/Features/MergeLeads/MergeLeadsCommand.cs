namespace Sankore.Modules.Leads.Features.MergeLeads;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

public sealed record MergeLeadsCommand(
    Guid TargetLeadId,
    Guid SourceLeadId,
    Guid MergedBy
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => TargetLeadId.ToString();
}

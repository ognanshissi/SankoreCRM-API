namespace Sankore.Modules.Leads.Features.SetQualificationCompleteness;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record SetQualificationCompletenessCommand(Guid LeadId, double Completeness)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}

namespace Sankore.Modules.Leads.Features.SetIntentLevel;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record SetIntentLevelCommand(Guid LeadId, LeadIntentLevel IntentLevel)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}

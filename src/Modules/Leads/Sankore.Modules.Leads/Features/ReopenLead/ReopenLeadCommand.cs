namespace Sankore.Modules.Leads.Features.ReopenLead;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>Transitions a New lead to Open.</summary>
internal sealed record ReopenLeadCommand(Guid LeadId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}

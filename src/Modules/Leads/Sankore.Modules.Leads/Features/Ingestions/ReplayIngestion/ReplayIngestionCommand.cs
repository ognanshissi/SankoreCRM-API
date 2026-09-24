namespace Sankore.Modules.Leads.Features.Ingestions.ReplayIngestion;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record ReplayIngestionCommand(Guid IngestionId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadIngestion";
    public string? ResourceId  => IngestionId.ToString();
}

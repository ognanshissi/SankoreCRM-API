namespace Sankore.Modules.Leads.Features.SlaConfigs.ActivateSlaConfig;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record ActivateSlaConfigCommand(Guid SlaConfigId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "SlaConfig";
    public string? ResourceId  => SlaConfigId.ToString();
}

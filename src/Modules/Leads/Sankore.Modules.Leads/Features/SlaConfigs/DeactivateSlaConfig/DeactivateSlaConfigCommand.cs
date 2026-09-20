namespace Sankore.Modules.Leads.Features.SlaConfigs.DeactivateSlaConfig;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record DeactivateSlaConfigCommand(Guid SlaConfigId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "SlaConfig";
    public string? ResourceId  => SlaConfigId.ToString();
}

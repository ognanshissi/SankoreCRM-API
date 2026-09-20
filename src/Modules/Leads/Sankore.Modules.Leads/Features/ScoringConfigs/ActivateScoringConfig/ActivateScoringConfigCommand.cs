namespace Sankore.Modules.Leads.Features.ScoringConfigs.ActivateScoringConfig;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record ActivateScoringConfigCommand(Guid ConfigId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ScoringConfig";
    public string? ResourceId  => ConfigId.ToString();
}

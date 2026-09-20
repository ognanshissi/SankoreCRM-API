namespace Sankore.Modules.Leads.Features.ScoringConfigs.UpdateScoringConfig;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record UpdateScoringConfigCommand(
    Guid ConfigId,
    string Name,
    int QualificationThreshold,
    double WeightDemographics,
    double WeightEngagement,
    double WeightProduct,
    double WeightChannel,
    double WeightRecency
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ScoringConfig";
    public string? ResourceId  => ConfigId.ToString();
}

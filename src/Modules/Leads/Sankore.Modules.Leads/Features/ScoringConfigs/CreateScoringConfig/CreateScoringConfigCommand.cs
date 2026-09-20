namespace Sankore.Modules.Leads.Features.ScoringConfigs.CreateScoringConfig;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record CreateScoringConfigCommand(
    Guid TenantId,
    string Name,
    int QualificationThreshold,
    double WeightDemographics,
    double WeightEngagement,
    double WeightProduct,
    double WeightChannel,
    double WeightRecency
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "ScoringConfig";
    public string? ResourceId  => null;
}

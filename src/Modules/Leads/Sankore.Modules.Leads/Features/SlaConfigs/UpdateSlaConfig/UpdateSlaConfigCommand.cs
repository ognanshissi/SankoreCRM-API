namespace Sankore.Modules.Leads.Features.SlaConfigs.UpdateSlaConfig;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record UpdateSlaConfigCommand(
    Guid SlaConfigId,
    string Name,
    TimeSpan FirstContactDeadline,
    TimeSpan QualificationDeadline,
    TimeSpan FollowUpDeadline,
    TimeSpan EscalationDeadline
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "SlaConfig";
    public string? ResourceId  => SlaConfigId.ToString();
}

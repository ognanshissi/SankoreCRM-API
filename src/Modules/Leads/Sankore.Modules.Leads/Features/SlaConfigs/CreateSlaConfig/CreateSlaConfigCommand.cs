namespace Sankore.Modules.Leads.Features.SlaConfigs.CreateSlaConfig;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record CreateSlaConfigCommand(
    Guid TenantId,
    Guid? AgencyId,
    string Name,
    TimeSpan FirstContactDeadline,
    TimeSpan QualificationDeadline,
    TimeSpan FollowUpDeadline,
    TimeSpan EscalationDeadline
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "SlaConfig";
    public string? ResourceId  => null;
}

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.ActivateAgency;

/// <summary>Re-activates a soft-deleted agency (reverses Deactivate).</summary>
public sealed record ActivateAgencyCommand(Guid AgencyId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Agency";
    public string? ResourceId => AgencyId.ToString();
}

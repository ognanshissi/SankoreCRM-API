using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.MoveAgency;

/// <summary>
/// Changes the parent of an agency (reparenting).
/// Pass null for <see cref="NewParentAgencyId"/> to promote the agency to root-level
/// (only valid when its AgencyType is HeadQuarter).
/// </summary>
public sealed record MoveAgencyCommand(
    Guid AgencyId,
    Guid? NewParentAgencyId
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Agency";
    public string? ResourceId => AgencyId.ToString();
}

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.AssignAgencyManager;

/// <summary>
/// Puts a user in charge of an agency (US: "as an administrator, I want to assign a manager of
/// agency"). Idempotent: re-sending the same manager succeeds and changes nothing.
/// </summary>
public sealed record AssignAgencyManagerCommand(
    Guid AgencyId,
    Guid ManagerUserId
) : IRequest<Result<AssignAgencyManagerResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Agency";
    public string? ResourceId => AgencyId.ToString();
}

/// <summary>
/// Echoes the resulting state so the caller need not re-read the agency, and can tell an
/// actual change from a replayed request.
/// </summary>
public sealed record AssignAgencyManagerResult(
    Guid AgencyId,
    Guid ManagerUserId,
    string ManagerFullName,
    Guid? PreviousManagerUserId,
    bool Changed,
    /// <summary>Role the manager now holds so the caller can show it without a second call.</summary>
    string GrantedRole);

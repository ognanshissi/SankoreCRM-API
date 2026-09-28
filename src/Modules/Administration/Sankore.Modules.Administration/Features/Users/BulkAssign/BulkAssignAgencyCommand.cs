using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.BulkAssign;

/// <summary>Moves a selection of users into one agency.</summary>
public sealed record BulkAssignAgencyCommand(IReadOnlyList<Guid> UserIds, Guid AgencyId)
    : IRequest<Result<BulkAssignResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Agency";

    // The agency is the thing being changed from the audit's point of view; the users are in
    // the payload, which AuditBehavior serializes in full.
    public string? ResourceId => AgencyId.ToString();
}

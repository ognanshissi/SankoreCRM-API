using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.DeleteAgency;

/// <summary>Soft-deletes an agency (IsDeleted = true, IsActive = false).</summary>
public sealed record DeleteAgencyCommand(Guid AgencyId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Agency";
    public string? ResourceId => AgencyId.ToString();
}

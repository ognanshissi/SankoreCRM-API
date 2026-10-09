namespace Sankore.Modules.Integration.Features.Mappings.DeleteMapping;

using MediatR;
using Sankore.Modules.Integration.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Removes one translation. A hard delete, unlike a client or an agency: a mapping carries no
/// history worth keeping — the audit row records who removed which code, which is the whole
/// trace needed — and a soft-deleted row would still have to be excluded from the unique index.
/// </summary>
internal sealed record DeleteMappingCommand(Guid ConnectionId, MappingDomain Domain, string CrmCode)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "IntegrationMapping";

    public string? ResourceId => ConnectionId.ToString();
}

namespace Sankore.Modules.Integration.Features.Connections.DeactivateConnection;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Stops routing the tenant's calls through this connection.
///
/// <para>
/// Deactivated, never deleted: commands, references and call logs point at it, and a deleted row
/// would make a year of audit trail unreadable. There is no DELETE in this area for that reason.
/// </para>
/// </summary>
internal sealed record DeactivateConnectionCommand(Guid ConnectionId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "IntegrationConnection";

    public string? ResourceId => ConnectionId.ToString();
}

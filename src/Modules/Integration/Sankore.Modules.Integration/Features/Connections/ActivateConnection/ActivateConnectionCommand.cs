namespace Sankore.Modules.Integration.Features.Connections.ActivateConnection;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Makes this connection the one the tenant's commands are sent through (INT-03, criterion 5).
///
/// <para>
/// Refused without a passed health check — the aggregate owns that rule, because activation is
/// what turns a row of coordinates into the destination of every write.
/// </para>
/// </summary>
internal sealed record ActivateConnectionCommand(Guid ConnectionId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "IntegrationConnection";

    public string? ResourceId => ConnectionId.ToString();
}

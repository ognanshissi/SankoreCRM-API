namespace Sankore.Modules.Integration.Features.Connections.CheckConnectionHealth;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Reaches the external system and records whether it answered (INT-03, criterion 4).
///
/// <para>
/// A command and not a query, even though the caller is only asking a question: the outcome is
/// WRITTEN on the connection, and it is what the activation gate reads. A query would leave
/// <c>LastHealthStatus</c> null forever and make activation impossible.
/// </para>
/// </summary>
internal sealed record CheckConnectionHealthCommand(Guid ConnectionId)
    : IRequest<Result<ConnectionHealthDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "IntegrationConnection";

    public string? ResourceId => ConnectionId.ToString();
}

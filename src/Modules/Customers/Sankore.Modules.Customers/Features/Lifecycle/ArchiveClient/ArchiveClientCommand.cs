namespace Sankore.Modules.Customers.Features.Lifecycle.ArchiveClient;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Archives a client (US-M01-BE-15): the soft end of life of the record. The row
/// is never deleted — it becomes read-only (<c>IsReadOnly</c>), keeps its whole
/// history, and only the retention/anonymization slice of the Compliance zone may
/// touch its personal data afterwards.
/// <paramref name="Reason"/> is an operator motive, intentionally kept in clear
/// text in the status history and in <c>ClientArchivedEvent</c>.
/// </summary>
public sealed record ArchiveClientCommand(Guid ClientId, string Reason)
    : IRequest<Result<ClientLifecycleStateDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

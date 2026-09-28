namespace Sankore.Modules.Customers.Features.Lifecycle.SuspendClient;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Suspends a client (US-M01-BE-13): the record stays readable and editable by
/// compliance, but the client is no longer commercially active.
///
/// No <c>[property: SensitiveData]</c> here on purpose: <paramref name="Reason"/>
/// is an operator-entered motive that is deliberately propagated in clear text to
/// the status history and to <c>ClientSuspendedEvent</c>. Masking it in the audit
/// trail would defeat the very purpose of the field.
/// </summary>
public sealed record SuspendClientCommand(Guid ClientId, string Reason)
    : IRequest<Result<ClientLifecycleStateDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

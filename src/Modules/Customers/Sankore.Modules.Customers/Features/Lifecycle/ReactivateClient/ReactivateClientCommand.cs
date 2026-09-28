namespace Sankore.Modules.Customers.Features.Lifecycle.ReactivateClient;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Lifts a suspension (US-M01-BE-13). The resulting status is decided by the
/// aggregate, not by the caller: <c>Active</c> when the KYC file is Approved,
/// otherwise back to <c>PendingKyc</c> — a suspended client whose KYC was never
/// completed must not silently become active again.
/// No sensitive field: the command carries an identifier only.
/// </summary>
public sealed record ReactivateClientCommand(Guid ClientId)
    : IRequest<Result<ClientLifecycleStateDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

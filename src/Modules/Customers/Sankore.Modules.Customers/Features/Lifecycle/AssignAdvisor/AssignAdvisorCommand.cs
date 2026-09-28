namespace Sankore.Modules.Customers.Features.Lifecycle.AssignAdvisor;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Assigns (or clears, with a null <paramref name="AdvisorUserId"/>) the account
/// officer who owns the relationship (US-M01-BE-16).
/// No sensitive field: identifiers only.
/// </summary>
public sealed record AssignAdvisorCommand(Guid ClientId, Guid? AdvisorUserId)
    : IRequest<Result<ClientLifecycleStateDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.DeactivateUser;

/// <summary>
/// Logically deactivates a user (Status → Disabled, DeactivatedAt = now).
/// Never physically deletes the row — preserves audit and assignment history.
/// Publishes <c>UserDeactivatedEvent</c> so the Leads module can reassign
/// the agent's active leads (US-M12-USER-001 Scenario 3).
/// </summary>
public sealed record DeactivateUserCommand(Guid UserId) : IRequest<Result>, ICommand;

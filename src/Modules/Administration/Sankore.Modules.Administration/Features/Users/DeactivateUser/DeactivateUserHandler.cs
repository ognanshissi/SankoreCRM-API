using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.DeactivateUser;

internal sealed class DeactivateUserHandler(
    AdministrationDbContext db,
    [FromKeyedServices(nameof(AdministrationDbContext))] IEventPublisher publisher
) : IRequestHandler<DeactivateUserCommand, Result>
{
    public async Task<Result> Handle(DeactivateUserCommand request, CancellationToken ct)
    {
        // 1. Load with tracking so EF picks up all mutations in one SaveChangesAsync.
        //    Filtered include: only active UserRole records — those are the ones we revoke.
        //    Tenant query filter is applied automatically on both User and UserRole.
        var user = await db.Users
            .AsTracking()
            .Include(u => u.UserRoles.Where(r => r.IsActive))
            .Where(u => u.Id == request.UserId)
            .SingleOrDefaultAsync(ct);

        if (user is null)
            return Result.Fail($"User {request.UserId} not found.");

        // 2. Domain guard — idempotency check (Gherkin S3: double-deactivation rejected).
        if (user.Status == UserStatus.Disabled)
            return Result.Fail("User is already disabled.");

        // 3. Domain transition — Status → Disabled, DeactivatedAt = now.
        var deactivatedEvent = user.Deactivate();

        // 4. Revoke all active role assignments (audit trail + prevents login via role checks).
        foreach (var userRole in user.UserRoles)
            userRole.Revoke();

        // 5. Persist: user state + role revocations committed atomically by TransactionBehavior.
        await db.SaveChangesAsync(ct);

        // 6. Publish integration event via Outbox.
        //    The Leads module subscribes to reassign this agent's active leads.
        await publisher.PublishAsync(deactivatedEvent, ct);

        // AuditBehavior writes AuditEntry automatically (ICommand marker).
        return Result.Ok();
    }
}

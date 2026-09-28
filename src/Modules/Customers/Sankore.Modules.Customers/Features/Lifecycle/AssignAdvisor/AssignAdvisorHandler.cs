namespace Sankore.Modules.Customers.Features.Lifecycle.AssignAdvisor;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class AssignAdvisorHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IAgencyDirectory agencyDirectory)
    : IRequestHandler<AssignAdvisorCommand, Result<ClientLifecycleStateDto>>
{
    public async Task<Result<ClientLifecycleStateDto>> Handle(
        AssignAdvisorCommand cmd, CancellationToken ct)
    {
        var client = await db.Clients
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ClientId, ct);

        if (client is null)
            return Result.Fail<ClientLifecycleStateDto>(CustomerErrors.ClientNotFound);

        // Out of perimeter answers NOT_FOUND, never OUT_OF_SCOPE (no existence leak).
        if (!await agencyScope.CanAccessAgencyAsync(
                currentUser.TenantId, currentUser.Id, client.AgencyId, ct))
            return Result.Fail<ClientLifecycleStateDto>(CustomerErrors.ClientNotFound);

        // Eligibility is owned by Administration (M14) and reached through the
        // IAgencyDirectory port: the advisor must be an active user AND belong to
        // the client's own agency. A null advisor clears the assignment, which needs
        // no check.
        if (cmd.AdvisorUserId is { } advisorUserId)
        {
            var eligible = await agencyDirectory.IsAdvisorEligibleAsync(
                client.TenantId, advisorUserId, client.AgencyId, ct);

            if (!eligible)
                return Result.Fail<ClientLifecycleStateDto>(CustomerErrors.AdvisorNotEligible);
        }

        var outcome = client.AssignAdvisor(cmd.AdvisorUserId, currentUser.Id);
        if (outcome.IsFailure)
            return Result.Fail<ClientLifecycleStateDto>(outcome.Error!);

        // No integration event: an advisor change is a portfolio detail that no other
        // module reacts to today. It stays fully traced by AuditBehavior (the command
        // is an ICommand + IResourceCommand).
        await db.SaveChangesAsync(ct);

        return Result.Ok(client.ToLifecycleState());
    }
}

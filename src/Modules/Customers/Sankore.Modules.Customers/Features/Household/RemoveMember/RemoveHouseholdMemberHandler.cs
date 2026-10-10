namespace Sankore.Modules.Customers.Features.Household.RemoveMember;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Household.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

internal sealed class RemoveHouseholdMemberHandler(
    CustomersDbContext db,
    HouseholdWriteContext context,
    ICurrentUser currentUser,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher)
    : IRequestHandler<RemoveHouseholdMemberCommand, Result<RemoveHouseholdMemberResult>>
{
    public async Task<Result<RemoveHouseholdMemberResult>> Handle(
        RemoveHouseholdMemberCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason))
            return Result.Fail<RemoveHouseholdMemberResult>(CustomerErrors.ReasonRequired);

        var loaded = await context.LoadAsync(cmd.ClientId, ct);
        if (loaded.IsFailure)
            return Result.Fail<RemoveHouseholdMemberResult>(loaded.Error!);

        var scope = loaded.Value;

        var member = scope.Household.FirstOrDefault(m => m.Id == cmd.MemberId);
        if (member is null)
            return Result.Fail<RemoveHouseholdMemberResult>(CustomerErrors.RelationshipNotFound);

        var now = context.Clock.GetUtcNow();
        member.Close(cmd.Reason, now);

        // A client-to-client spouse link has a mirror row on the other client, and closing one has
        // to close the other — otherwise one of the two clients still shows a marriage the other
        // has ended. The pair is what ReciprocalRelationshipId exists for.
        if (member.ReciprocalRelationshipId is { } mirrorId)
        {
            var mirror = await db.ClientRelationships
                .AsTracking()
                .FirstOrDefaultAsync(r => r.Id == mirrorId, ct);

            mirror?.Close(cmd.Reason, now);
        }

        // ── The designations the departing member held (BNF-B-13) ────────────
        //
        // Flagged for review, never deleted, and never silently reallocated: the remaining
        // designations of the rank no longer total 100%, and that is exactly what the agent has to
        // be shown. Loaded WITH their designations because FlagDesignationsOf walks the collection.
        var holdings = await db.ProductHoldings
            .AsTracking()
            .Include(h => h.Designations)
            .Where(h => h.ClientId == cmd.ClientId
                        && h.Designations.Any(d =>
                            d.SubjectId == cmd.MemberId && d.ValidTo == null))
            .ToListAsync(ct);

        var flagged = 0;
        var affected = new List<Guid>();

        foreach (var holding in holdings)
        {
            var count = holding.FlagDesignationsOf(cmd.MemberId, "HOUSEHOLD_MEMBER_REMOVED");
            if (count == 0) continue;

            flagged += count;
            affected.Add(holding.Id);
        }

        var dependents = scope.Household.Count(m =>
            m.IsActive && m.Type is RelationshipType.Dependent or RelationshipType.Child);

        scope.Client.RecomputeDependentsCount(dependents);

        // Only when something was actually flagged. A removal that affects no designation is
        // ordinary housekeeping, and notifying on it would train the recipient to ignore the
        // notifications that matter.
        if (flagged > 0)
        {
            await publisher.PublishAsync(
                new HouseholdMemberRemovedEvent(
                    scope.Client.TenantId, cmd.ClientId, cmd.MemberId,
                    flagged, affected, currentUser.Id),
                ct);
        }

        // CommitAsync recomputes the exposure over the household as it stands AFTER this write —
        // the removed member is excluded because it reads IsActive, which is now false.
        await context.CommitAsync(scope, ct);

        return Result.Ok(new RemoveHouseholdMemberResult(flagged, affected));
    }
}

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

namespace Sankore.Modules.Customers.Features.Relationships.CloseRelationship;

internal sealed class CloseRelationshipHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope
) : IRequestHandler<CloseRelationshipCommand, Result>
{
    public async Task<Result> Handle(CloseRelationshipCommand request, CancellationToken ct)
    {
        var client = await db.Clients
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == request.ClientId, ct);

        if (client is null ||
            !await agencyScope.CanAccessAgencyAsync(currentUser.TenantId, currentUser.Id, client.AgencyId, ct))
            return Result.Fail(CustomerErrors.ClientNotFound);

        if (client.IsReadOnly)
            return Result.Fail(CustomerErrors.ClientReadOnly);

        var relationship = await db.ClientRelationships
            .AsTracking()
            .FirstOrDefaultAsync(r => r.Id == request.RelationshipId && r.ClientId == request.ClientId, ct);

        if (relationship is null)
            return Result.Fail(CustomerErrors.RelationshipNotFound);

        var at = DateTimeOffset.UtcNow;

        // Close() only dates ValidTo and is a no-op on an already closed row, so a
        // retry stays safe and history is never rewritten.
        relationship.Close(request.Reason, at);

        // ── Reciprocity ─────────────────────────────────────────────────────
        // A Spouse link exists as two mirrored rows. Closing one and leaving the
        // other open would show a client married to someone who is not married to
        // them, so the mirror is closed in the same transaction.
        if (relationship.ReciprocalRelationshipId is { } reciprocalId)
        {
            var reciprocal = await db.ClientRelationships
                .AsTracking()
                .FirstOrDefaultAsync(r => r.Id == reciprocalId, ct);

            reciprocal?.Close(request.Reason, at);

            // The mirror belongs to the other client, whose dependents count must be
            // recomputed too when the closed type counted towards it.
            if (reciprocal is not null)
                await RecomputeDependentsAsync(reciprocal.ClientId, ct);
        }

        client.RecomputeDependentsCount(await CountActiveDependentsAsync(request.ClientId, ct));

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    /// <summary>
    /// Recomputes the count on a client this handler does not already have tracked
    /// (the other end of a reciprocal link).
    /// </summary>
    private async Task RecomputeDependentsAsync(Guid clientId, CancellationToken ct)
    {
        var other = await db.Clients.AsTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (other is null)
            return;

        other.RecomputeDependentsCount(await CountActiveDependentsAsync(clientId, ct));
    }

    /// <summary>
    /// Counts from the rows rather than decrementing, so the value cannot drift.
    /// The row closed above is still tracked as not-yet-saved, hence the in-memory
    /// <c>ValidTo</c> filter on top of the database count.
    /// </summary>
    private async Task<int> CountActiveDependentsAsync(Guid clientId, CancellationToken ct)
    {
        var candidates = await db.ClientRelationships
            .AsTracking()
            .Where(r => r.ClientId == clientId
                        && (r.Type == RelationshipType.Dependent || r.Type == RelationshipType.Child))
            .ToListAsync(ct);

        return candidates.Count(r => r.ValidTo is null);
    }
}

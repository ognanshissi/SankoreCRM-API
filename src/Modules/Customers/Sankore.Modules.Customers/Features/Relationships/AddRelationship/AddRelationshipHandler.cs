using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Relationships.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

namespace Sankore.Modules.Customers.Features.Relationships.AddRelationship;

internal sealed class AddRelationshipHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IFieldEncryptor encryptor,
    IBlindIndexer indexer,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher
) : IRequestHandler<AddRelationshipCommand, Result<AddRelationshipResult>>
{
    public async Task<Result<AddRelationshipResult>> Handle(
        AddRelationshipCommand request, CancellationToken ct)
    {
        // Tracked: DependentsCount is recomputed on the aggregate after the write.
        var client = await db.Clients
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == request.ClientId, ct);

        if (client is null ||
            !await agencyScope.CanAccessAgencyAsync(currentUser.TenantId, currentUser.Id, client.AgencyId, ct))
            return Result.Fail<AddRelationshipResult>(CustomerErrors.ClientNotFound);

        if (client.IsReadOnly)
            return Result.Fail<AddRelationshipResult>(CustomerErrors.ClientReadOnly);

        var tenantId = currentUser.TenantId;
        var actor = currentUser.Id;
        var validFrom = DateTimeOffset.UtcNow;
        var toAnotherClient = request.RelatedClientId is { } id && id != Guid.Empty;

        ClientRelationship relationship;
        ClientRelationship? reciprocal = null;

        if (toAnotherClient)
        {
            var relatedClientId = request.RelatedClientId!.Value;

            // Checked before the lookup: a self-relationship is a modelling mistake,
            // not a missing record, and deserves its own code.
            if (relatedClientId == request.ClientId)
                return Result.Fail<AddRelationshipResult>(CustomerErrors.SelfRelationshipForbidden);

            var related = await db.Clients.FirstOrDefaultAsync(c => c.Id == relatedClientId, ct);

            // Same rule as for the subject: out of perimeter reads as missing.
            if (related is null ||
                !await agencyScope.CanAccessAgencyAsync(tenantId, currentUser.Id, related.AgencyId, ct))
                return Result.Fail<AddRelationshipResult>(CustomerErrors.ClientNotFound);

            // An archived or merged client accepts no new link either — and a Spouse
            // link would have to write a mirror row onto it.
            if (related.IsReadOnly)
                return Result.Fail<AddRelationshipResult>(CustomerErrors.ClientReadOnly);

            relationship = ClientRelationship.ToClient(
                tenantId, request.ClientId, request.Type, relatedClientId, validFrom, actor);

            // ── Reciprocity ─────────────────────────────────────────────────
            // Marriage is symmetric: both clients must show the link, and the two
            // rows point at each other so closing one closes the other.
            if (request.Type == RelationshipType.Spouse)
            {
                reciprocal = ClientRelationship.ToClient(
                    tenantId, relatedClientId, RelationshipType.Spouse, request.ClientId, validFrom, actor);

                relationship.LinkReciprocal(reciprocal.Id);
                reciprocal.LinkReciprocal(relationship.Id);
            }
        }
        else
        {
            // The third party's phone and document number are encrypted at rest; the
            // phone also gets a blind index so a guarantor can be found by number.
            var phone = string.IsNullOrWhiteSpace(request.ExternalPhoneNumber)
                ? null
                : request.ExternalPhoneNumber.Trim();

            var document = string.IsNullOrWhiteSpace(request.ExternalDocumentNumber)
                ? null
                : request.ExternalDocumentNumber.Trim();

            relationship = ClientRelationship.ToExternal(
                tenantId,
                request.ClientId,
                request.Type,
                request.ExternalFullName!.Trim(),
                phone is null ? null : encryptor.Encrypt(phone),
                phone is null ? null : indexer.Compute(BlindIndexPurpose.Phone, phone),
                request.ExternalDateOfBirth,
                document is null ? null : encryptor.Encrypt(document),
                validFrom,
                actor);
        }

        db.ClientRelationships.Add(relationship);
        if (reciprocal is not null)
            db.ClientRelationships.Add(reciprocal);

        // ── Dependents ──────────────────────────────────────────────────────
        // Recomputed from the active rows rather than incremented, so a replay or a
        // closed relationship can never drift the count.
        var activeDependents = await db.ClientRelationships.CountAsync(
            r => r.ClientId == request.ClientId
                 && r.ValidTo == null
                 && (r.Type == RelationshipType.Dependent || r.Type == RelationshipType.Child),
            ct);

        if (DependentRelationshipTypes.Counts(request.Type))
            activeDependents++;

        client.RecomputeDependentsCount(activeDependents);

        // A guarantor changes the client's credit standing, so M04 (Loans) and the
        // notification module must hear about it. Published into the outbox inside
        // the same transaction as the write: the event cannot exist without the row.
        if (request.Type == RelationshipType.Guarantor)
        {
            await publisher.PublishAsync(
                new GuarantorLinkedEvent(
                    tenantId,
                    request.ClientId,
                    toAnotherClient ? request.RelatedClientId : null,
                    toAnotherClient ? null : relationship.ExternalFullName,
                    actor),
                ct);
        }

        await db.SaveChangesAsync(ct);

        return Result.Ok(new AddRelationshipResult(
            relationship.Id,
            relationship.ReciprocalRelationshipId,
            activeDependents));
    }
}

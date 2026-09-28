namespace Sankore.Modules.Customers.Features.Clients.UpdateClientSensitive;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Domain.Events;
using Sankore.Modules.Customers.Domain.Matching;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// US-M01-BE-08.
///
/// The returned list, the audit entry and the published event all carry field NAMES only.
/// No old value, no new value, not even encrypted: a consumer that genuinely needs the
/// content must call the audited reveal endpoint under its own identity, which is what
/// makes every access to a regulated field individually traceable.
///
/// The postal address is not part of the aggregate's identity method — it is a contact
/// point — so the slice closes the previous one and opens a new one, keeping the old
/// value auditable instead of overwriting it.
/// </summary>
internal sealed class UpdateClientSensitiveHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IDuplicateProbe duplicateProbe,
    IFieldEncryptor encryptor,
    IBlindIndexer indexer,
    IPhoneticKeyCalculator phoneticKeys,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher
) : IRequestHandler<UpdateClientSensitiveCommand, Result<IReadOnlyList<string>>>
{
    public async Task<Result<IReadOnlyList<string>>> Handle(
        UpdateClientSensitiveCommand cmd, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;
        var actor = currentUser.Id;
        var now = DateTimeOffset.UtcNow;

        // The address lives in the child collection, so it must be loaded for the
        // close-and-reopen below.
        var client = await db.Clients
            .AsTracking()
            .Include(c => c.ContactPoints)
            .FirstOrDefaultAsync(c => c.Id == cmd.ClientId, ct);

        if (client is null)
            return Result.Fail<IReadOnlyList<string>>(CustomerErrors.ClientNotFound);

        if (!await agencyScope.CanAccessAgencyAsync(tenantId, actor, client.AgencyId, ct))
            return Result.Fail<IReadOnlyList<string>>(CustomerErrors.ClientNotFound);

        if (client.Version != cmd.ExpectedVersion)
            return Result.Fail<IReadOnlyList<string>>(CustomerErrors.ConcurrencyConflict);

        if (client.IsReadOnly)
            return Result.Fail<IReadOnlyList<string>>(CustomerErrors.ClientReadOnly);

        // Re-checked in the handler even though the validator covers it: this slice can
        // also be dispatched from another handler (or a test) that bypasses the
        // validation pipeline, and a compliance change without a motive must never land.
        if (string.IsNullOrWhiteSpace(cmd.Reason) || cmd.Reason.Trim().Length < MinimumReasonLength)
            return Result.Fail<IReadOnlyList<string>>(CustomerErrors.ReasonRequired);

        // ── A new document number must not already belong to somebody else ────
        string? encryptedDocumentNumber = null;
        string? documentBlindIndex = null;

        if (!string.IsNullOrWhiteSpace(cmd.IdentityDocumentNumber))
        {
            var documentNumber = cmd.IdentityDocumentNumber.Trim();
            documentBlindIndex = indexer.Compute(BlindIndexPurpose.IdentityDocument, documentNumber);

            // excludeClientId: re-submitting the client's OWN number (a no-op edit, or a
            // change of the issue date only) must not collide with itself.
            var hit = await duplicateProbe.FindByIdentityDocumentAsync(
                tenantId, documentBlindIndex, excludeClientId: client.Id, ct);

            if (hit is not null)
                return Result.Fail<IReadOnlyList<string>>(CustomerErrors.DuplicateIdentityDocument);

            encryptedDocumentNumber = encryptor.Encrypt(documentNumber);
        }

        // The aggregate computes the diff itself and raises a domain event with it;
        // reading the events raised BY THIS CALL is more reliable than re-deriving the
        // diff here, where trimming and normalization rules would have to be duplicated.
        var eventsBefore = client.DomainEvents.Count;

        var update = client.UpdateSensitiveIdentity(
            firstName: cmd.FirstName,
            lastName: cmd.LastName,
            maidenName: cmd.MaidenName,
            docType: cmd.IdentityDocumentType,
            encryptedDocNumber: encryptedDocumentNumber,
            docNumberBlindIndex: documentBlindIndex,
            docIssuedOn: cmd.IdentityDocumentIssuedOn,
            docExpiresOn: cmd.IdentityDocumentExpiresOn,
            actor: actor);

        if (update.IsFailure)
            return Result.Fail<IReadOnlyList<string>>(update.Error!);

        var changedFields = client.DomainEvents
            .Skip(eventsBefore)
            .OfType<ClientSensitiveFieldsChangedDomainEvent>()
            .SelectMany(e => e.Fields)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // A name change invalidates the phonetic keys the nightly duplicate detector
        // blocks on — stale keys would make the corrected record unmatchable.
        if (changedFields.Contains(nameof(Client.FirstName), StringComparer.Ordinal)
            || changedFields.Contains(nameof(Client.LastName), StringComparer.Ordinal))
        {
            client.SetPhoneticKeys(
                phoneticKeys.Compute(client.LastName),
                phoneticKeys.Compute(client.FirstName));
        }

        // ── Address: close the old contact point, open a new one ─────────────
        var addressLine = cmd.Address?.ToSingleLineOrNull();
        if (addressLine is not null)
        {
            var addressBlindIndex = indexer.Compute(BlindIndexPurpose.PostalAddress, addressLine);

            var currentAddress = client.ContactPoints
                .FirstOrDefault(cp => cp.IsActive && cp.Type == ContactPointType.Address);

            // Compared on the blind index, not on the ciphertext: AES-GCM uses a fresh
            // nonce per call, so the same address encrypts to a different string every
            // time and a ciphertext comparison would always report a change.
            var isNewValue = currentAddress is null
                             || !string.Equals(currentAddress.BlindIndex, addressBlindIndex, StringComparison.Ordinal);

            if (isNewValue)
            {
                if (currentAddress is not null)
                {
                    var close = client.CloseContactPoint(currentAddress.Id, actor, now);
                    if (close.IsFailure)
                        return Result.Fail<IReadOnlyList<string>>(close.Error!);
                }

                client.AddContactPoint(
                    ContactPointType.Address,
                    encryptor.Encrypt(addressLine)
                        ?? throw new InvalidOperationException(
                            "Field encryption returned null for a non-null value; check Customers:FieldEncryptionKey."),
                    addressBlindIndex,
                    label: currentAddress?.Label,
                    isPrimary: true,
                    validFrom: now,
                    actor: actor);

                changedFields.Add(nameof(SensitiveField.PostalAddress));
            }
        }

        // No event when nothing moved: an empty ClientSensitiveDataChangedEvent would
        // make every KYC consumer re-open a file for a no-op edit.
        if (changedFields.Count > 0)
        {
            await publisher.PublishAsync(
                new ClientSensitiveDataChangedEvent(
                    TenantId: tenantId,
                    ClientId: client.Id,
                    ChangedFields: changedFields,
                    Reason: cmd.Reason.Trim(),
                    ActorUserId: actor),
                ct);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Fail<IReadOnlyList<string>>(CustomerErrors.ConcurrencyConflict);
        }

        return Result.Ok<IReadOnlyList<string>>(changedFields);
    }

    /// <summary>
    /// Mirrors <c>UpdateClientSensitiveValidator</c>: a motive shorter than this is not a
    /// justification an auditor can use.
    /// </summary>
    private const int MinimumReasonLength = 10;
}

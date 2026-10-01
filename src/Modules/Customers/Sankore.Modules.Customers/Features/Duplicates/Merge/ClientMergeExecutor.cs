using Sankore.Modules.Kyc.PublicApi;

namespace Sankore.Modules.Customers.Features.Duplicates.Merge;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Domain.Matching;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <inheritdoc cref="IClientMergeExecutor"/>
internal sealed class ClientMergeExecutor(
    CustomersDbContext db,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher,
    IPhoneticKeyCalculator phoneticKeys,
    TimeProvider clock,
    ILogger<ClientMergeExecutor> logger)
    : IClientMergeExecutor
{
    /// <summary>Marker written on links that lost their meaning because both ends became the survivor.</summary>
    private const string SelfLinkCloseReason = "MERGE_SELF_LINK";

    /// <summary>Marker written on an absorbed membership the survivor already held.</summary>
    private const string DuplicateMembershipCloseReason = "MERGE_DUPLICATE_MEMBERSHIP";

    public async Task<Result> ExecuteAsync(ClientMergeRequest request, Guid actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tenantId = request.TenantId;
        var now = clock.GetUtcNow();

        // Both aggregates are loaded tracked, with their contact points, so every write below lands
        // in the single transaction the calling ICommand handler owns. IgnoreQueryFilters is paired
        // with an explicit tenant predicate: the executor also runs from background identities.
        var survivor = await db.Clients
            .AsTracking()
            .IgnoreQueryFilters()
            .Include(c => c.ContactPoints)
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == request.SurvivorClientId, ct);

        var absorbed = await db.Clients
            .AsTracking()
            .IgnoreQueryFilters()
            .Include(c => c.ContactPoints)
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == request.AbsorbedClientId, ct);

        if (survivor is null || absorbed is null)
            return Result.Fail(CustomerErrors.ClientNotFound);

        if (survivor.Id == absorbed.Id)
            return Result.Fail(CustomerErrors.SameClientMergeForbidden);

        if (absorbed.Status == ClientStatus.Merged)
            return Result.Fail(CustomerErrors.ClientAlreadyMerged);

        if (survivor.IsReadOnly)
            return Result.Fail(CustomerErrors.ClientReadOnly);

        // Captured before anything moves: the comparison that decides whether KYC must be reopened.
        var survivorKyc = survivor.KycStatus;
        var absorbedKyc = absorbed.KycStatus;

        var applied = ApplyFieldChoices(survivor, absorbed, request.FieldChoicesJson, actorUserId);
        if (applied.IsFailure)
            return applied;

        await ReattachChildrenAsync(survivor, absorbed, tenantId, now, ct);

        var merged = absorbed.MarkMerged(survivor.Id, actorUserId);
        if (merged.IsFailure)
            return merged;

        await CloseDuplicateCandidateAsync(survivor.Id, absorbed.Id, tenantId, actorUserId, now, ct);

        var executed = request.MarkExecuted(now);
        if (executed.IsFailure)
            return executed;

        // Published before SaveChanges on purpose: the outbox row is written by the same
        // transaction as the merge, so the event cannot exist without the merge (or vice versa).
        await publisher.PublishAsync(
            new ClientsMergedEvent(tenantId, survivor.Id, absorbed.Id, actorUserId), ct);

        // Diverging KYC verdicts where one side was approved means the surviving file now mixes a
        // validated identity with a non-validated one: M02 has to look at it again. The event
        // carries the field NAME only, never a value.
        if (survivorKyc != absorbedKyc && (survivorKyc == KycStatus.Approved || absorbedKyc == KycStatus.Approved))
        {
            await publisher.PublishAsync(
                new ClientSensitiveDataChangedEvent(
                    tenantId,
                    survivor.Id,
                    [nameof(Client.KycStatus)],
                    "MERGE_KYC_REVIEW",
                    actorUserId),
                ct);
        }

        logger.LogInformation(
            "Merged client {AbsorbedClientId} into {SurvivorClientId} for tenant {TenantId} (request {MergeRequestId}).",
            absorbed.Id, survivor.Id, tenantId, request.Id);

        return Result.Ok();
    }

    // ── Field arbitration ───────────────────────────────────────────────────

    /// <summary>
    /// Applies the requester's per-field choices onto the survivor. Only the fields listed in
    /// <see cref="ClientMergeFields"/> are considered; anything else is dropped without a word,
    /// because a front-end sending a field this version does not know must not fail a merge.
    /// </summary>
    private Result ApplyFieldChoices(Client survivor, Client absorbed, string fieldChoicesJson, Guid actorUserId)
    {
        var choices = ParseChoices(fieldChoicesJson);

        bool FromAbsorbed(string field) =>
            choices.TryGetValue(field, out var choice)
            && string.Equals(choice, ClientMergeFields.Absorbed, StringComparison.OrdinalIgnoreCase);

        if (survivor.Type == ClientType.Legal)
        {
            var legal = survivor.UpdateLegalIdentity(
                legalName: FromAbsorbed(ClientMergeFields.LegalName) ? absorbed.LegalName : null,
                legalFormCode: FromAbsorbed(ClientMergeFields.LegalFormCode) ? absorbed.LegalFormCode : null,
                encryptedRegistrationNumber: FromAbsorbed(ClientMergeFields.RegistrationNumber)
                    ? absorbed.EncryptedRegistrationNumber : null,
                registrationNumberBlindIndex: FromAbsorbed(ClientMergeFields.RegistrationNumber)
                    ? absorbed.RegistrationNumberBlindIndex : null,
                encryptedTaxIdNumber: FromAbsorbed(ClientMergeFields.TaxIdNumber)
                    ? absorbed.EncryptedTaxIdNumber : null,
                incorporationDate: FromAbsorbed(ClientMergeFields.IncorporationDate)
                    ? absorbed.IncorporationDate : null,
                actor: actorUserId);

            if (legal.IsFailure)
                return legal;
        }
        else
        {
            // The identity document is arbitrated as a whole: type, ciphertext, blind index and both
            // dates always describe the SAME document, so splitting the choice would build a
            // Frankenstein record whose blind index no longer matches its number.
            var takeDocument = FromAbsorbed(ClientMergeFields.IdentityDocument);

            var identity = survivor.UpdateSensitiveIdentity(
                firstName: FromAbsorbed(ClientMergeFields.FirstName) ? absorbed.FirstName : null,
                lastName: FromAbsorbed(ClientMergeFields.LastName) ? absorbed.LastName : null,
                maidenName: FromAbsorbed(ClientMergeFields.MaidenName) ? absorbed.MaidenName : null,
                docType: takeDocument ? absorbed.IdentityDocumentType : null,
                encryptedDocNumber: takeDocument ? absorbed.EncryptedIdentityDocumentNumber : null,
                docNumberBlindIndex: takeDocument ? absorbed.IdentityDocumentNumberBlindIndex : null,
                docIssuedOn: takeDocument ? absorbed.IdentityDocumentIssuedOn : null,
                docExpiresOn: takeDocument ? absorbed.IdentityDocumentExpiresOn : null,
                actor: actorUserId);

            if (identity.IsFailure)
                return identity;

            ApplyUnmutatedFields(survivor, absorbed, FromAbsorbed);
        }

        var nonSensitive = survivor.UpdateNonSensitive(
            profession: FromAbsorbed(ClientMergeFields.Profession) ? absorbed.Profession : null,
            employer: FromAbsorbed(ClientMergeFields.Employer) ? absorbed.Employer : null,
            maritalStatus: FromAbsorbed(ClientMergeFields.MaritalStatus) ? absorbed.MaritalStatus : null,
            encryptedDeclaredIncome: FromAbsorbed(ClientMergeFields.DeclaredIncome)
                ? absorbed.EncryptedDeclaredIncome : null,
            declaredIncomeCurrency: FromAbsorbed(ClientMergeFields.DeclaredIncome)
                ? absorbed.DeclaredIncomeCurrency : null,
            preferredLanguage: FromAbsorbed(ClientMergeFields.PreferredLanguage)
                ? absorbed.PreferredLanguage : null,
            actor: actorUserId);

        if (nonSensitive.IsFailure)
            return nonSensitive;

        if (FromAbsorbed(ClientMergeFields.AdvisorUserId))
        {
            var advisor = survivor.AssignAdvisor(absorbed.AdvisorUserId, actorUserId);
            if (advisor.IsFailure)
                return advisor;
        }

        // Names may have moved, and the phonetic keys are what future detection blocks on: recompute
        // them unconditionally rather than trying to guess whether a name actually changed.
        if (survivor.Type == ClientType.Legal)
            survivor.SetPhoneticKeys(phoneticKeys.Compute(survivor.LegalName), null);
        else
            survivor.SetPhoneticKeys(
                phoneticKeys.Compute(survivor.LastName), phoneticKeys.Compute(survivor.FirstName));

        return Result.Ok();
    }

    /// <summary>
    /// Civil-status fields the aggregate exposes no mutator for (they are set once at creation and
    /// never edited by a feature). Writing them through the tracked entry keeps the change inside
    /// the same transaction and under the same <c>xmin</c> concurrency token as everything else —
    /// the alternative would be widening the aggregate's public surface for a single caller.
    /// </summary>
    private void ApplyUnmutatedFields(Client survivor, Client absorbed, Func<string, bool> fromAbsorbed)
    {
        var entry = db.Entry(survivor);

        if (fromAbsorbed(ClientMergeFields.Gender))
            entry.Property(c => c.Gender).CurrentValue = absorbed.Gender;

        if (fromAbsorbed(ClientMergeFields.DateOfBirth))
        {
            // Ciphertext and blind index move together or the index stops matching the value.
            entry.Property(c => c.EncryptedDateOfBirth).CurrentValue = absorbed.EncryptedDateOfBirth;
            entry.Property(c => c.DateOfBirthBlindIndex).CurrentValue = absorbed.DateOfBirthBlindIndex;
        }

        if (fromAbsorbed(ClientMergeFields.BirthPlace))
            entry.Property(c => c.BirthPlace).CurrentValue = absorbed.BirthPlace;

        if (fromAbsorbed(ClientMergeFields.Nationality))
            entry.Property(c => c.Nationality).CurrentValue = absorbed.Nationality;

        if (fromAbsorbed(ClientMergeFields.FatherName))
            entry.Property(c => c.FatherName).CurrentValue = absorbed.FatherName;

        if (fromAbsorbed(ClientMergeFields.MotherName))
            entry.Property(c => c.MotherName).CurrentValue = absorbed.MotherName;
    }

    private static Dictionary<string, string> ParseChoices(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (raw is null)
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var choices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in raw)
            {
                // Unknown field names are dropped here, silently, as the contract requires.
                if (ClientMergeFields.All.Contains(key))
                    choices[key] = value;
            }

            return choices;
        }
        catch (JsonException)
        {
            // A corrupted payload must not lose the merge: everything then defaults to the survivor.
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // ── Reattachment ────────────────────────────────────────────────────────

    /// <summary>
    /// Moves everything the absorbed client owns onto the survivor. Nothing is ever deleted:
    /// links that no longer make sense are CLOSED with a reason, so the merge stays auditable.
    /// </summary>
    private async Task ReattachChildrenAsync(
        Client survivor, Client absorbed, Guid tenantId, DateTimeOffset now, CancellationToken ct)
    {
        // ── Contact points ──────────────────────────────────────────────────
        // Only the active ones follow: a closed phone belongs to the history of the record that
        // owned it. AttachContactPoint keeps the survivor's own primaries primary, so no type ever
        // ends up with two.
        foreach (var contactPoint in absorbed.ContactPoints.Where(cp => cp.IsActive).ToList())
            survivor.AttachContactPoint(contactPoint);

        // ── Relationships ───────────────────────────────────────────────────
        var relationships = await db.ClientRelationships
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId
                     && (r.ClientId == absorbed.Id || r.RelatedClientId == absorbed.Id))
            .ToListAsync(ct);

        foreach (var relationship in relationships)
        {
            if (relationship.ClientId == absorbed.Id)
                relationship.ReassignTo(survivor.Id);

            if (relationship.RelatedClientId == absorbed.Id)
                db.Entry(relationship).Property(r => r.RelatedClientId).CurrentValue = survivor.Id;

            // A link that pointed from one of the two records to the other would now point at
            // itself, which the domain forbids outright — close it instead.
            if (relationship.ClientId == survivor.Id && relationship.RelatedClientId == survivor.Id)
                relationship.Close(SelfLinkCloseReason, now);
        }

        // ── Group memberships ───────────────────────────────────────────────
        var memberships = await db.GroupMemberships
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && (m.ClientId == absorbed.Id || m.ClientId == survivor.Id))
            .ToListAsync(ct);

        var survivorLiveGroups = memberships
            .Where(m => m.ClientId == survivor.Id && m.IsActive)
            .Select(m => m.GroupId)
            .ToHashSet();

        foreach (var membership in memberships.Where(m => m.ClientId == absorbed.Id).ToList())
        {
            if (membership.IsActive && survivorLiveGroups.Contains(membership.GroupId))
            {
                // The survivor is already a live member of that group: a second open row would
                // violate ux_group_memberships_active, so this one is closed where it stands and
                // stays on the absorbed record as evidence of the duplicate.
                membership.Leave(DuplicateMembershipCloseReason, now);
                continue;
            }

            membership.ReassignTo(survivor.Id);

            if (membership.IsActive)
                survivorLiveGroups.Add(membership.GroupId);
        }

        // ── Beneficial owners ───────────────────────────────────────────────
        var owners = await db.BeneficialOwners
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId
                     && (o.LegalClientId == absorbed.Id || o.LinkedClientId == absorbed.Id))
            .ToListAsync(ct);

        foreach (var owner in owners)
        {
            if (owner.LegalClientId == absorbed.Id)
                db.Entry(owner).Property(o => o.LegalClientId).CurrentValue = survivor.Id;

            if (owner.LinkedClientId == absorbed.Id)
                db.Entry(owner).Property(o => o.LinkedClientId).CurrentValue = survivor.Id;

            // Same self-reference guard: a legal entity cannot be its own beneficial owner.
            if (owner.IsActive && owner.LegalClientId == survivor.Id && owner.LinkedClientId == survivor.Id)
                owner.Close(now);
        }

        // ── Timeline ────────────────────────────────────────────────────────
        // The whole history follows, closed entries included: the surviving file must tell the
        // complete story of both records. DedupKey is unaffected, so the unique index still holds.
        var timeline = await db.ClientTimelineEntries
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId && e.ClientId == absorbed.Id)
            .ToListAsync(ct);

        foreach (var entry in timeline)
            entry.ReassignTo(survivor.Id);
    }

    private async Task CloseDuplicateCandidateAsync(
        Guid survivorId, Guid absorbedId, Guid tenantId, Guid actorUserId, DateTimeOffset now, CancellationToken ct)
    {
        // The candidate row is stored canonically (ClientAId < ClientBId), so the pair is looked up
        // in that order whichever of the two ended up surviving.
        var (aId, bId) = survivorId.CompareTo(absorbedId) <= 0
            ? (survivorId, absorbedId)
            : (absorbedId, survivorId);

        var candidate = await db.DuplicateCandidates
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.ClientAId == aId && d.ClientBId == bId, ct);

        // A merge requested by hand has no candidate row, which is perfectly normal.
        candidate?.MarkMerged(actorUserId, now);
    }
}

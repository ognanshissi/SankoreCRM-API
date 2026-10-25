namespace Sankore.Modules.Customers.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;

/// <summary>
/// The single implementation of <see cref="ICustomersModule"/> — everything other
/// modules are allowed to ask M01 synchronously.
///
/// All reads use <c>IgnoreQueryFilters()</c> with an explicit tenant predicate: the
/// caller may be a Hangfire job or a MassTransit consumer whose ambient
/// <c>ITenantContext</c> is not the tenant in the request, and in that situation the
/// global filter would silently return nothing (or, worse, be satisfied by the wrong
/// tenant). The predicate is never omitted.
///
/// Nothing on this facade decrypts a protected field. <see cref="ClientSummary"/> holds
/// identifiers, a display name and enum names only; a module that genuinely needs a
/// phone number or a document number must go through M01's audited reveal endpoint so
/// the access is logged against a named user.
/// </summary>
internal sealed class CustomersModuleFacade(
    CustomersDbContext db,
    ILeadConversionService leadConversion,
    Sankore.Shared.Infrastructure.Crypto.IBlindIndexer indexer,
    Features.Holdings.Sync.HoldingSyncService holdingSync,
    Notifications.ClientEmailNotifier clientEmails,
    RemoteIdentity.RemoteIdentityVerifier remoteIdentity,
    RemoteIdentity.ReadIdentityComparer readIdentity,
    Notifications.ClientSmsNotifier clientSms,
    Sankore.Shared.Infrastructure.Crypto.IFieldEncryptor? encryptor = null,
    Sankore.Shared.Kernel.Authorization.IAgencyScopeProvider? agencyScope = null,
    Microsoft.Extensions.Logging.ILogger<CustomersModuleFacade>? logger = null,
    Features.Clients.Shared.ClientVisibility? visibility = null)
    : ICustomersModule
{
    /// <summary>
    /// The statuses a client must be in to count as "a customer making a request". Archived and
    /// Merged are excluded: attaching a lead to an archived record would revive it by the side
    /// door, and to a merged one would point the request at an id that no longer answers.
    /// PendingKyc IS included — a client whose file is still being built is very much a customer,
    /// and refusing to recognise them is how a second record for the same person gets created.
    /// </summary>
    private static readonly ClientStatus[] AttachableStatuses =
    [
        ClientStatus.PendingKyc,
        ClientStatus.Active,
        ClientStatus.Suspended,
        ClientStatus.KycRejected,
    ];

    // ── The back-office side of a holding (BNF-B-16) ────────────────────────
    //
    // Straight through to HoldingSyncService, which explains why these three are not behind
    // MediatR like the rest of the zone.

    public Task<HoldingForSync?> GetHoldingForSyncAsync(
        Guid tenantId, Guid holdingId, CancellationToken ct)
        => holdingSync.GetForSyncAsync(tenantId, holdingId, ct);

    public Task<Result> ConfirmHoldingSyncAsync(
        Guid tenantId, Guid holdingId, string externalSystem, string externalRef, CancellationToken ct)
        => holdingSync.ConfirmAsync(tenantId, holdingId, externalSystem, externalRef, ct);

    public Task<Result> RejectHoldingSyncAsync(
        Guid tenantId, Guid holdingId, string reason, CancellationToken ct)
        => holdingSync.RejectAsync(tenantId, holdingId, reason, ct);

    public Task<IReadOnlyDictionary<Guid, HoldingSyncState>> GetHoldingSyncStatesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> holdingIds, CancellationToken ct)
        => holdingSync.GetSyncStatesAsync(tenantId, holdingIds, ct);

    /// <summary>
    /// Straight through to the conversion seam (BNF-B-24): no logic here, because the MediatR
    /// pipeline is where the guarantees are.
    /// </summary>
    public Task<Result<Guid>> CreateHoldingFromLeadAsync(
        CreateHoldingFromLeadRequest request, CancellationToken ct)
        => leadConversion.CreateHoldingFromLeadAsync(request, ct);

    /// <summary>La même chose pour une demande gagnée, par le même seam.</summary>
    public Task<Result<Guid>> CreateHoldingFromOpportunityAsync(
        CreateHoldingFromOpportunityRequest request, CancellationToken ct)
        => leadConversion.CreateHoldingFromOpportunityAsync(request, ct);

    /// <inheritdoc />
    public async Task<bool> CanActorAccessClientAsync(
        Guid tenantId, Guid clientId, Guid actorUserId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || clientId == Guid.Empty || actorUserId == Guid.Empty)
            return false;

        ArgumentNullException.ThrowIfNull(agencyScope);

        // The agency only — nothing else of the client is read, because nothing else is needed and
        // a projection that grew would be a projection a caller starts depending on.
        var client = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.Id == clientId)
            .Select(c => new { c.AgencyId, c.AdvisorUserId, c.CreatedBy })
            .FirstOrDefaultAsync(ct);

        if (client is null)
            return false;

        // Assigned-only reads. The rule needs the actor's permissions, which only the ambient user
        // carries: it applies when the actor IS the signed-in caller — every caller of this
        // contract today passes exactly that.
        if (visibility?.RestrictedTo is { } restricted && restricted == actorUserId
            && !Features.Clients.Shared.ClientVisibility.IsAdvisedBy(client.AdvisorUserId, client.CreatedBy, actorUserId))
            return false;

        return await agencyScope.CanAccessAgencyAsync(tenantId, actorUserId, client.AgencyId, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The whole of it is four steps, and the order is the point: find the client, check the
    /// caller's perimeter, pick the slot's contact point, decrypt. Nothing is written — see the
    /// contract for why this act is traced by the telephony module's own row rather than by a
    /// <c>SensitiveDataAccessLog</c> entry.
    /// </remarks>
    public Task<bool> IsProcessingRestrictedAsync(Guid tenantId, Guid clientId, CancellationToken ct)
        => db.Clients
            .IgnoreQueryFilters()
            .AnyAsync(c => c.TenantId == tenantId && c.Id == clientId && c.ProcessingRestrictedAt != null, ct);

    public async Task<Result<string>> ResolvePhoneForCallAsync(
        Guid tenantId, Guid clientId, PhoneSlot slot, Guid actorUserId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || clientId == Guid.Empty)
            return Result.Fail<string>(CustomerErrors.ClientNotFound);

        // IgnoreQueryFilters with an explicit tenant predicate, like every method here: the
        // caller is another module's handler and the ambient tenant is not guaranteed to be this
        // one. AttachableStatuses is deliberately NOT applied — an agent may well have to ring a
        // client whose file is suspended, and that is a commercial decision rather than this
        // method's.
        var client = await db.Clients
            .IgnoreQueryFilters()
            .Include(c => c.ContactPoints)
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == clientId, ct);

        if (client is null)
            return Result.Fail<string>(CustomerErrors.ClientNotFound);

        // Required here and nowhere else on this facade — see the constructor's remarks. Throwing
        // beats degrading: a perimeter check that silently did not happen is the one failure this
        // method must not have.
        ArgumentNullException.ThrowIfNull(agencyScope);
        ArgumentNullException.ThrowIfNull(encryptor);

        // Folded into "not found", never a 403: a 403 confirms that another branch's client
        // exists, which is the one thing the perimeter is there to hide.
        if (!await agencyScope.CanAccessAgencyAsync(tenantId, actorUserId, client.AgencyId, ct))
            return Result.Fail<string>(CustomerErrors.ClientNotFound);

        // Data-purge lot 3: a person who asked to be forgotten is not called any more.
        if (client.ProcessingRestrictedAt is not null)
            return Result.Fail<string>(CustomerErrors.ProcessingRestricted);

        // Primary first, then most recently valid — the same ordering RevealSensitiveFieldHandler
        // uses to pick "the" phone, so the number an agent sees on the screen is the number the
        // Primary slot dials.
        var phones = client.ContactPoints
            .Where(cp => cp.IsActive && cp.Type == ContactPointType.Phone)
            .OrderByDescending(cp => cp.IsPrimary)
            .ThenByDescending(cp => cp.ValidFrom)
            .ToList();

        var contactPoint = slot switch
        {
            PhoneSlot.Primary => phones.ElementAtOrDefault(0),
            PhoneSlot.Secondary => phones.ElementAtOrDefault(1),
            _ => null,
        };

        if (contactPoint?.EncryptedValue is not { } ciphertext)
            return Result.Fail<string>(CustomerErrors.UnknownSensitiveField);

        string? clear;

        try
        {
            clear = encryptor.Decrypt(ciphertext);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException
                                      or System.Security.Cryptography.CryptographicException
                                      or ArgumentException)
        {
            // A rotated key or data copied between environments. The caller learns "this slot has
            // no dialable number" and nothing about the stored bytes.
            logger?.LogError(
                ex, "Could not decrypt the {Slot} phone of client {ClientId} in tenant {TenantId} "
                  + "for a click-to-call.", slot, clientId, tenantId);

            return Result.Fail<string>(CustomerErrors.UnknownSensitiveField);
        }

        return string.IsNullOrWhiteSpace(clear)
            ? Result.Fail<string>(CustomerErrors.UnknownSensitiveField)
            : Result.Ok(clear.Trim());
    }

    public async Task<IReadOnlyList<CustomerMatch>> FindMatchesAsync(
        Guid tenantId,
        string? phoneNumber,
        string? email,
        string? identityDocumentNumber,
        CancellationToken ct)
    {
        // M01 indexes the clear values with ITS OWN key — the caller could not have done it, which
        // is why the contract takes clear text. See ICustomersModule.FindMatchesAsync.
        var phoneIndex = Index(BlindIndexPurpose.Phone, phoneNumber);
        var emailIndex = Index(BlindIndexPurpose.Email, email);
        var docIndex   = Index(BlindIndexPurpose.IdentityDocument, identityDocumentNumber);

        if (phoneIndex is null && emailIndex is null && docIndex is null)
            return [];

        // Contact points first: phone and e-mail live on the child table, and a client may hold
        // the same value on several rows (a number labelled "personal" and "shop"), so the ids are
        // collapsed before they reach the client query. ValidTo == null — a closed contact point
        // is a number the client USED to have, and a lead reaching that number is not evidence
        // about this client any more.
        var contactHits = phoneIndex is null && emailIndex is null
            ? []
            : await db.ClientContactPoints
                .IgnoreQueryFilters()
                .Where(cp => cp.TenantId == tenantId
                          && cp.ValidTo == null
                          && ((phoneIndex != null && cp.Type == ContactPointType.Phone
                                                  && cp.BlindIndex == phoneIndex)
                           || (emailIndex != null && cp.Type == ContactPointType.Email
                                                  && cp.BlindIndex == emailIndex)))
                .Select(cp => new { cp.ClientId, cp.Type })
                .Distinct()
                .ToListAsync(ct);

        var byPhone = contactHits.Where(h => h.Type == ContactPointType.Phone)
                                 .Select(h => h.ClientId).ToHashSet();
        var byEmail = contactHits.Where(h => h.Type == ContactPointType.Email)
                                 .Select(h => h.ClientId).ToHashSet();

        var candidateIds = byPhone.Union(byEmail).ToList();

        var clients = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && AttachableStatuses.Contains(c.Status)
                     && (candidateIds.Contains(c.Id)
                      || (docIndex != null && c.IdentityDocumentNumberBlindIndex == docIndex)))
            .Select(c => new
            {
                c.Id, c.ClientNumber, c.DisplayName, c.AgencyId, c.AdvisorUserId,
                Status = c.Status, Kyc = c.KycStatus, c.IdentityDocumentNumberBlindIndex
            })
            .ToListAsync(ct);

        return
        [
            .. clients.Select(c =>
            {
                var matchedOn = new List<string>(3);
                if (byPhone.Contains(c.Id)) matchedOn.Add("phone");
                if (byEmail.Contains(c.Id)) matchedOn.Add("email");
                if (docIndex is not null && c.IdentityDocumentNumberBlindIndex == docIndex)
                    matchedOn.Add("identityDocument");

                return new CustomerMatch(
                    c.Id, c.ClientNumber, c.DisplayName, c.AgencyId, c.AdvisorUserId,
                    c.Status.ToString(), c.Kyc.ToString(), matchedOn);
            })
            // Most corroborated first: a client matched on document AND phone is a better
            // suggestion than one matched on a shared household phone alone.
            .OrderByDescending(m => m.MatchedOn.Count)
            .ThenBy(m => m.ClientNumber, StringComparer.Ordinal)
        ];
    }

    private string? Index(BlindIndexPurpose purpose, string? value)
        => string.IsNullOrWhiteSpace(value) ? null : indexer.Compute(purpose, value);

    /// <summary>
    /// Depth limit while walking the merge chain. A chain is normally one or two hops;
    /// the cap turns a corrupted cycle (A merged into B merged into A) into a bounded
    /// wrong answer instead of an infinite loop holding a request thread.
    /// </summary>
    private const int MaxMergeHops = 20;

    public async Task<ClientSummary?> GetClientSummaryAsync(Guid tenantId, Guid clientId, CancellationToken ct)
        => await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.Id == clientId)
            .Select(c => new ClientSummary(
                c.Id,
                c.ClientNumber,
                c.Type.ToString(),
                c.DisplayName,
                c.Status.ToString(),
                c.AgencyId,
                c.AdvisorUserId,
                c.KycStatus.ToString(),
                c.RiskLevel.ToString(),
                c.MergedIntoId))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, ClientSummary>> GetClientSummariesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> clientIds, CancellationToken ct)
    {
        // A List, not the incoming collection and not an array: on .NET 10 an array's Contains
        // binds to the ReadOnlySpan<T> extension and no longer translates to SQL, which would
        // turn this into a client-side evaluation over every client in the tenant.
        var ids = clientIds.Distinct().ToList();

        // No round-trip for nothing: a page whose rows carry no customer reference would otherwise
        // issue `IN ()`.
        if (ids.Count == 0)
            return new Dictionary<Guid, ClientSummary>();

        var rows = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && ids.Contains(c.Id))
            .Select(c => new ClientSummary(
                c.Id,
                c.ClientNumber,
                c.Type.ToString(),
                DisplayName: (c.Type == ClientType.Individual ? c.DisplayName : c.LegalName)!,
                c.Status.ToString(),
                c.AgencyId,
                c.AdvisorUserId,
                c.KycStatus.ToString(),
                c.RiskLevel.ToString(),
                c.MergedIntoId))
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Id);
    }

    public async Task<Guid?> ResolveClientIdAsync(Guid tenantId, Guid clientId, CancellationToken ct)
    {
        var current = clientId;
        Guid? deepestKnown = null;

        for (var hop = 0; hop < MaxMergeHops; hop++)
        {
            var node = await db.Clients
                .IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId && c.Id == current)
                .Select(c => new { c.Id, c.MergedIntoId })
                .FirstOrDefaultAsync(ct);

            // Nothing at the first hop means the client simply does not exist.
            // Nothing at a later hop means the chain dangles, so the best answer is
            // the last record we did find rather than a misleading null.
            if (node is null)
                return deepestKnown;

            deepestKnown = node.Id;

            if (node.MergedIntoId is null || node.MergedIntoId == node.Id)
                return node.Id;

            current = node.MergedIntoId.Value;
        }

        // Chain longer than the cap, or cyclic: return the furthest record reached.
        return deepestKnown;
    }

    public Task<bool> ExistsAndActiveAsync(Guid tenantId, Guid clientId, CancellationToken ct)
        => db.Clients
            .IgnoreQueryFilters()
            .AnyAsync(
                c => c.TenantId == tenantId
                  && c.Id == clientId
                  && c.Status == ClientStatus.Active,
                ct);

    public Task<Result<CreateFromLeadResult>> CreateFromLeadAsync(CreateFromLeadRequest request, CancellationToken ct)
        => leadConversion.CreateFromLeadAsync(request, ct);

    /// <summary>
    /// The legacy contract says "exists"; the honest mapping is "exists and is usable".
    /// Its only caller gates conversion on it, and attaching business to a suspended,
    /// archived or merged record would be wrong — so an inactive client answers false.
    /// </summary>
    public Task<bool> ExistsAsync(Guid tenantId, Guid customerId, CancellationToken ct)
        => ExistsAndActiveAsync(tenantId, customerId, ct);

    /// <summary>
    /// Whether the origin of the funds has been recorded. A boolean out of a projection that
    /// never loads the narrative: the caller is a KYC approval gate, and the sentence behind the
    /// answer describes a named person's assets.
    ///
    /// <para>
    /// A CODE is what counts as recorded, not the narrative. The code comes from a closed list and
    /// is what an institution can report on; a free-text sentence with no code is work somebody
    /// started and did not finish.
    /// </para>
    /// </summary>
    /// <summary>
    /// The clients whose contract anniversary falls today. Narrowed to LIVE holdings: a closed or
    /// rejected contract has no anniversary to honour.
    ///
    /// <para>
    /// The day-and-month comparison is done in memory over the live holdings of the tenant rather
    /// than in SQL. Matching a recurring date in PostgreSQL means <c>EXTRACT</c> on two columns,
    /// which no index covers, and the leap-day fold below cannot be expressed that way at all —
    /// while a tenant's live portfolio is the set this job already has to walk once a night.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<Guid>> GetClientsWithContractAnniversaryAsync(
        Guid tenantId, DateOnly onDay, CancellationToken ct)
    {
        // IgnoreQueryFilters with an explicit tenant predicate: the caller is a Hangfire job in
        // another module, running under SYSTEM identity with no ambient tenant of its own.
        var candidates = await db.ProductHoldings
            .IgnoreQueryFilters()
            .Where(h => h.TenantId == tenantId
                     && h.Status != HoldingStatus.Closed
                     && h.Status != HoldingStatus.Rejected)
            .Select(h => new
            {
                h.ClientId,
                // Reached through the typed jsonb, so the fallback stays one expression.
                EffectiveFrom = h.Specifics.EffectiveFrom,
                h.SignedAt,
            })
            .ToListAsync(ct);

        var anniversaries = candidates
            .Select(h => h.EffectiveFrom
                ?? (h.SignedAt.HasValue
                    ? DateOnly.FromDateTime(h.SignedAt.Value.UtcDateTime)
                    : (DateOnly?)null))
            .Zip(candidates, (started, h) => (Started: started, h.ClientId))
            // A holding with neither date has no anniversary. Skipped rather than given an
            // arbitrary one: a review raised on a date nobody agreed to is noise a compliance
            // officer learns to ignore.
            .Where(x => x.Started is not null && FallsOn(x.Started!.Value, onDay))
            .Select(x => x.ClientId)
            .Distinct()
            .ToList();

        return anniversaries;
    }

    /// <summary>
    /// Whether <paramref name="onDay"/> is an anniversary of <paramref name="started"/>.
    ///
    /// <para>
    /// The year the contract began is NOT an anniversary of itself — a file brought up to date on
    /// the day it was opened is the opening, not an update.
    /// </para>
    ///
    /// <para>
    /// 29 February folds onto 28 February in a non-leap year, so a contract signed on a leap day
    /// is reviewed every year rather than once every four.
    /// </para>
    /// </summary>
    private static bool FallsOn(DateOnly started, DateOnly onDay)
    {
        if (onDay.Year <= started.Year) return false;

        if (started.Month == onDay.Month && started.Day == onDay.Day) return true;

        var leapDayInACommonYear =
            started is { Month: 2, Day: 29 } &&
            onDay is { Month: 2, Day: 28 } &&
            !DateTime.IsLeapYear(onDay.Year);

        return leapDayInACommonYear;
    }

    public async Task<bool> HasRecordedFundsOriginAsync(
        Guid tenantId, Guid clientId, CancellationToken ct)
        // IgnoreQueryFilters with an explicit tenant predicate: the caller is a consumer or an
        // approval handler in another module, whose ambient tenant is not necessarily this one.
        => await db.Clients
            .IgnoreQueryFilters()
            .AnyAsync(
                c => c.TenantId == tenantId
                  && c.Id == clientId
                  && c.FundsOriginCode != null
                  && c.FundsOriginCode != "",
                ct);

    // ── Reaching a client by email (KRM-B-03) ───────────────────────────────
    //
    // Straight through to ClientEmailNotifier, for the reason the three holding-sync members above
    // are: the work is a side effect on another system, not a read this module models. Both stay
    // deliberately thin — the address never appears in either signature.

    public Task<bool> HasEmailAsync(Guid tenantId, Guid clientId, CancellationToken ct)
        => clientEmails.HasEmailAsync(tenantId, clientId, ct);

    public Task<string?> GetMaskedEmailAsync(Guid tenantId, Guid clientId, CancellationToken ct)
        => clientEmails.GetMaskedEmailAsync(tenantId, clientId, ct);

    public Task<bool> NotifyClientAsync(NotifyClientRequest request, CancellationToken ct)
        => clientEmails.NotifyAsync(request, ct);

    // The SMS twin. Straight through for the same reason, and the service does the one piece of
    // real work this channel needs: turning a number stored as it was dialled into E.164 with the
    // tenant's calling code.
    public Task<bool> HasPhoneAsync(Guid tenantId, Guid clientId, CancellationToken ct)
        => clientSms.HasPhoneAsync(tenantId, clientId, ct);

    public Task<string?> GetMaskedPhoneAsync(
        Guid tenantId, Guid clientId, CancellationToken ct)
        => clientSms.GetMaskedPhoneAsync(tenantId, clientId, ct);

    public Task<bool> NotifyClientBySmsAsync(
        NotifyClientBySmsRequest request, CancellationToken ct)
        => clientSms.NotifyAsync(request, ct);

    // KRM-B-05. Straight through, and the signature is the whole design: a boolean out, no value
    // in either direction, and the comparison in one place that holds the key.
    public Task<bool> VerifyRemoteIdentityAsync(
        Guid tenantId, Guid clientId, string? clientNumber, DateOnly? birthDate, CancellationToken ct)
        => remoteIdentity.VerifyAsync(tenantId, clientId, clientNumber, birthDate, ct);

    // KRM-B-08, and the same shape for the same reason: the caller sends what it read, this module
    // compares it against the record it holds, and a boolean comes back. The normalisation rule
    // ("sans accents ni casse") is SearchKeyBuilder's, which already defines it for client search.
    public Task<bool> MatchesReadIdentityAsync(
        Guid tenantId, Guid clientId, string? readSurname, string? readGivenNames,
        DateOnly? readBirthDate, CancellationToken ct)
        => readIdentity.MatchesAsync(
            tenantId, clientId, readSurname, readGivenNames, readBirthDate, ct);

    /// <summary>
    /// Email and PhoneNumber are deliberately left null: both are encrypted at rest in
    /// M01 and only reachable through the audited reveal endpoint. No sensitive data
    /// crosses this boundary — a legacy caller that needs a contact detail has to ask
    /// for it explicitly, under its own user's identity, and be logged doing so.
    /// </summary>
    public async Task<CustomerSummary?> GetCustomerAsync(Guid tenantId, Guid customerId, CancellationToken ct)
    {
        var summary = await GetClientSummaryAsync(tenantId, customerId, ct);

        return summary is null
            ? null
            : new CustomerSummary(
                Id: summary.Id,
                DisplayName: summary.DisplayName,
                Email: null,
                PhoneNumber: null,
                AgencyId: summary.AgencyId);
    }

}

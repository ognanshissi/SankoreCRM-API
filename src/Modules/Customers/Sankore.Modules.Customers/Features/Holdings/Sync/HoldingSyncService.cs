namespace Sankore.Modules.Customers.Features.Holdings.Sync;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Holdings.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// The back-office side of a holding's life (BNF-B-16) — what Integration reads before sending a
/// subscription, and what it writes back when the far end answers.
///
/// <para>
/// <b>Not behind MediatR, unlike every other write in this zone.</b> The two write methods are
/// called from a MassTransit consumer in ANOTHER module, under the SYSTEM identity, about a
/// holding whose agency perimeter means nothing there: <c>HoldingWriteContext</c> would check the
/// consumer's perimeter and <c>AuditBehavior</c> would record SYSTEM as the actor of a change the
/// insurer made. The traceability that matters here is the holding's own
/// <c>ExternalRef</c>/<c>SyncedAt</c> and the Integration command row, both of which survive.
/// </para>
///
/// <para>
/// <b>Public, unlike the rest of this zone</b>, because the two MassTransit consumers that drive
/// it are public — the broker instantiates them — and a public type cannot take an internal
/// dependency in its constructor. The methods are still reachable only through the module's own
/// consumers and its facade.
/// </para>
///
/// <para>
/// Every query uses <c>IgnoreQueryFilters</c> with an explicit tenant predicate: the ambient
/// tenant context belongs to whichever request or consumer is running, and this service is asked
/// about a tenant by name.
/// </para>
/// </summary>
public sealed class HoldingSyncService(
    CustomersDbContext db,
    IAdministrationModule administration,
    SubjectLabelResolver labels,
    TimeProvider clock)
{
    /// <summary>
    /// The holding, its terms and its current clause. Null when it no longer exists in that
    /// tenant — a consumer must not fail on a holding somebody closed in between.
    /// </summary>
    public async Task<HoldingForSync?> GetForSyncAsync(
        Guid tenantId, Guid holdingId, CancellationToken ct)
    {
        var holding = await db.ProductHoldings
            .IgnoreQueryFilters()
            .Include(h => h.Designations)
            .FirstOrDefaultAsync(h => h.TenantId == tenantId && h.Id == holdingId, ct);

        if (holding is null)
            return null;

        // The line's IntegrationSystem decides whether anything is sent at all, and only the
        // catalogue knows it. An outage leaves it null, which the consumer reads as "do not send"
        // — the safe direction: a subscription not yet pushed is retried, one pushed to the wrong
        // place is not recalled.
        string? externalSystem = null;
        try
        {
            using var suppressed = new System.Transactions.TransactionScope(
                System.Transactions.TransactionScopeOption.Suppress,
                System.Transactions.TransactionScopeAsyncFlowOption.Enabled);

            var line = await administration.GetBeneficiaryPolicyAsync(
                tenantId, holding.ProductLineId, ct);

            suppressed.Complete();
            externalSystem = line?.IntegrationSystem;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
        }

        var current = holding.CurrentDesignations.ToList();
        var resolved = await labels.ResolveAsync(current, ct);
        var birthDates = await BirthDatesAsync(tenantId, holding.ClientId, current, ct);

        return new HoldingForSync(
            HoldingId:          holding.Id,
            ClientId:           holding.ClientId,
            ProductLineId:      holding.ProductLineId,
            ProductCode:        holding.ProductCode,
            ProductCategory:    holding.ProductCategory,
            Status:             holding.Status.ToString(),
            ClauseVersion:      holding.ClauseVersion,
            IsSigned:           holding.SignedAt is not null && holding.SignatureReference is not null,
            SignedAt:           holding.SignedAt,
            SignatureReference: holding.SignatureReference,
            ExternalSystem:     externalSystem,
            InsuredCapital:     holding.Specifics.InsuredCapital,
            Premium:            holding.Specifics.Premium,
            Periodicity:        holding.Specifics.Periodicity,
            Currency:           holding.Specifics.Currency,
            EffectiveFrom:      holding.Specifics.EffectiveFrom,
            SourceOpportunityId: holding.SourceOpportunityId,
            Designations:
            [
                .. current
                    .OrderBy(d => d.Rank)
                    .ThenBy(d => d.RoleCode)
                    .Select(d => new HoldingDesignationForSync(
                        RoleCode:     d.RoleCode,
                        FullName:     d.SubjectId is { } id && resolved.TryGetValue(id, out var name)
                                          ? name
                                          : d.ExternalFullName ?? "—",
                        Relationship: d.ExternalRelationship,
                        Rank:         d.Rank,
                        SharePercent: d.SharePercent,
                        BirthDate:    d.SubjectId is { } subject && birthDates.TryGetValue(subject, out var dob)
                                          ? dob
                                          : null))
            ]);
    }

    /// <summary>
    /// The birth dates of designated HOUSEHOLD MEMBERS, which an insurer needs to rate a
    /// beneficiary clause.
    /// </summary>
    /// <remarks>
    /// Household members only, deliberately. A designated CLIENT's date of birth is an encrypted
    /// column with its own audited reveal rules, and reading it here would route around them; a
    /// member's sits on the relationship row in clear, as the existing
    /// <c>ExternalDateOfBirth</c> column always has. So the clause reaches the insurer with the
    /// dates it can have and no others, rather than with a reveal nobody authorised.
    /// </remarks>
    private async Task<Dictionary<Guid, DateOnly>> BirthDatesAsync(
        Guid tenantId,
        Guid clientId,
        IEnumerable<BeneficiaryDesignation> designations,
        CancellationToken ct)
    {
        var memberIds = designations
            .Where(d => d.SubjectType == DesignationSubjectType.HouseholdMember
                     && d.SubjectId is not null)
            .Select(d => d.SubjectId!.Value)
            .Distinct()
            .ToList();

        if (memberIds.Count == 0)
            return [];

        // IgnoreQueryFilters is paired with BOTH predicates, and both are load-bearing.
        //
        // The tenant one is the module's standing rule for a filterless query, and it is the one
        // this method was missing: a designation's SubjectId is a Guid somebody supplied, so
        // without it a relationship id belonging to another tenant would have its date of birth
        // read here and carried into the payload sent to an insurer. Unguessable ids are not a
        // control — the pairing is.
        //
        // The CLIENT one narrows it further: a date of birth is only ever needed for a member of
        // THIS holding's household, so a relationship of another client of the same tenant has no
        // business being read either. Belt and braces with the designation-time validation in
        // DesignationSubjectValidator, deliberately: that one stops a bad row being written, this
        // one stops a bad row that already exists from being acted on.
        var rows = await db.ClientRelationships
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId
                     && r.ClientId == clientId
                     && memberIds.Contains(r.Id)
                     && r.ExternalDateOfBirth != null)
            .Select(r => new { r.Id, r.ExternalDateOfBirth })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Id, r => r.ExternalDateOfBirth!.Value);
    }

    /// <summary>
    /// The sync state of several holdings at once (BNF-B-17).
    /// </summary>
    /// <remarks>
    /// Four scalars and nothing else — no clause, no designation, no name. A nightly comparison
    /// has no business reading who is designated, and a projection that carried it would be a
    /// personal-data read happening on a schedule with nobody asking.
    ///
    /// <para>
    /// A holding the tenant does not have is ABSENT from the result rather than present with
    /// nulls: the caller must be able to tell "no such holding" from "inactive".
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<Guid, HoldingSyncState>> GetSyncStatesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> holdingIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(holdingIds);

        if (holdingIds.Count == 0)
            return new Dictionary<Guid, HoldingSyncState>();

        // A List and not the collection as given: on .NET 10 an array's Contains binds to the
        // ReadOnlySpan<T> extension and no longer translates to SQL — the repo-wide rule.
        var ids = holdingIds.Distinct().ToList();

        var rows = await db.ProductHoldings
            .IgnoreQueryFilters()
            .Where(h => h.TenantId == tenantId && ids.Contains(h.Id))
            .Select(h => new { h.Id, h.Status, h.ExternalRef, h.ClauseVersion })
            .ToListAsync(ct);

        return rows.ToDictionary(
            r => r.Id,
            r => new HoldingSyncState(
                HoldingId:     r.Id,
                Status:        r.Status.ToString(),
                ExternalRef:   r.ExternalRef,
                ClauseVersion: r.ClauseVersion,
                // Decided HERE from the status, not left to the caller to re-derive from a string:
                // PendingSync is not in force yet and Rejected never was, and a comparison that
                // guessed would report a divergence on every holding still in a queue.
                IsLive:        r.Status == HoldingStatus.Active));
    }

    /// <summary>
    /// The far end accepted it. Idempotent on the same reference; a DIFFERENT one is refused.
    /// </summary>
    public async Task<Result> ConfirmAsync(
        Guid tenantId, Guid holdingId, string externalSystem, string externalRef, CancellationToken ct,
        bool awaitingPayment = false)
    {
        if (string.IsNullOrWhiteSpace(externalRef))
            return Result.Fail(CustomerErrors.ExternalRefRequired);

        var holding = await LoadAsync(tenantId, holdingId, ct);
        if (holding is null)
            return Result.Fail(CustomerErrors.HoldingNotFound);

        // A redelivered success. Already Active with this reference is the outcome the caller
        // asked for, so it succeeds — and with a different one it must not, because two external
        // references for one contract means one of the two systems is wrong.
        if (holding.Status is HoldingStatus.Active or HoldingStatus.AwaitingPayment)
        {
            return string.Equals(holding.ExternalRef, externalRef.Trim(), StringComparison.Ordinal)
                ? Result.Ok()
                : Result.Fail(CustomerErrors.HoldingExternalRefConflict);
        }

        var confirmed = holding.ConfirmSync(externalSystem, externalRef, clock.GetUtcNow(), awaitingPayment);
        if (confirmed.IsFailure)
            return confirmed;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    /// <summary>The far end refused it, with a reason an agent can act on.</summary>
    /// <summary>
    /// The premium of a contract sent without it is confirmed (insurance lot 2). Only a holding
    /// awaiting payment moves; one still PendingSync becomes Active when the insurer answers, the
    /// premium being no longer outstanding by then.
    /// </summary>
    public async Task<Result> ConfirmPaymentAsync(Guid tenantId, Guid holdingId, CancellationToken ct)
    {
        var holding = await LoadAsync(tenantId, holdingId, ct);
        if (holding is null)
            return Result.Fail(CustomerErrors.HoldingNotFound);

        if (holding.Status != HoldingStatus.AwaitingPayment)
            return Result.Ok();

        var paid = holding.ConfirmPayment(clock.GetUtcNow());
        if (paid.IsFailure)
            return paid;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    public async Task<Result> CloseAsync(Guid tenantId, Guid holdingId, string reason, CancellationToken ct)
    {
        var holding = await LoadAsync(tenantId, holdingId, ct);
        if (holding is null)
            return Result.Fail(CustomerErrors.HoldingNotFound);

        var closed = holding.Close(reason, clock.GetUtcNow());
        if (closed.IsFailure)
            return closed;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    public async Task<Result> RejectAsync(
        Guid tenantId, Guid holdingId, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Fail(CustomerErrors.ReasonRequired);

        var holding = await LoadAsync(tenantId, holdingId, ct);
        if (holding is null)
            return Result.Fail(CustomerErrors.HoldingNotFound);

        // A redelivered rejection: already Rejected is the outcome the caller asked for. The
        // reason is NOT overwritten — the first one is the one the agent has been looking at.
        if (holding.Status == HoldingStatus.Rejected)
            return Result.Ok();

        var rejected = holding.RecordRejection(reason, clock.GetUtcNow());
        if (rejected.IsFailure)
            return rejected;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    private Task<ProductHolding?> LoadAsync(Guid tenantId, Guid holdingId, CancellationToken ct)
        => db.ProductHoldings
            .IgnoreQueryFilters()
            .AsTracking()
            .Include(h => h.Designations)
            .FirstOrDefaultAsync(h => h.TenantId == tenantId && h.Id == holdingId, ct);
}

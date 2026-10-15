namespace Sankore.Modules.Customers.Features.Holdings.Shared;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Where a commercial outcome came from, and who recorded it.
///
/// <para>
/// Exactly one origin id is set, and the two factories are what guarantees it: a holding carries
/// a lead or a request, never both and never neither. Expressing that as a record with two
/// nullable ids and no factories would make "neither" representable, and the first caller to
/// produce it would open a draft that nothing can ever be traced back to.
/// </para>
/// </summary>
internal sealed record DraftHoldingOrigin
{
    private DraftHoldingOrigin() { }

    public Guid TenantId { get; private init; }
    public Guid ClientId { get; private init; }
    public Guid ProductLineId { get; private init; }
    public Guid? SourceLeadId { get; private init; }
    public Guid? SourceOpportunityId { get; private init; }

    /// <summary>Who is recorded as having opened it — the agent whose act produced the outcome.</summary>
    public Guid OpenedByUserId { get; private init; }

    /// <summary>What the logs name this origin by. Only ever read by <see cref="DraftHoldingOpener"/>.</summary>
    public string Describe()
        => SourceLeadId is { } lead ? $"lead {lead}" : $"opportunity {SourceOpportunityId}";

    public static DraftHoldingOrigin FromLead(
        Guid tenantId, Guid clientId, Guid productLineId, Guid sourceLeadId, Guid openedByUserId)
        => new()
        {
            TenantId = tenantId,
            ClientId = clientId,
            ProductLineId = productLineId,
            SourceLeadId = sourceLeadId,
            OpenedByUserId = openedByUserId,
        };

    public static DraftHoldingOrigin FromOpportunity(
        Guid tenantId, Guid clientId, Guid productLineId, Guid sourceOpportunityId, Guid openedByUserId)
        => new()
        {
            TenantId = tenantId,
            ClientId = clientId,
            ProductLineId = productLineId,
            SourceOpportunityId = sourceOpportunityId,
            OpenedByUserId = openedByUserId,
        };
}

/// <summary>
/// Opens the DRAFT holding a commercial outcome leaves behind — a converted lead (BNF-B-24) or a
/// won request.
///
/// <para>
/// <b>One body for both, on purpose.</b> Three of its four gates are not about where the outcome
/// came from at all: the client must exist, the product line must resolve, and
/// <c>AllowMultipleHoldings</c> must permit a second contract. A second copy of them would be a
/// second chance to get the last one wrong — and wrong in the permissive direction opens two
/// contracts for one request, which is the thing a back office cannot undo.
/// </para>
///
/// <para>
/// It does NOT go through <see cref="HoldingWriteContext"/>, and that is deliberate: the context
/// checks the caller's AGENCY perimeter through <c>ICurrentUser</c>, while both callers run on
/// M13's own path where the ambient user is the agent whose act produced the outcome. The
/// perimeter question has already been answered — they converted the lead, or they closed the
/// request — and re-asking it here is how a legitimate outcome fails on a race.
/// </para>
/// </summary>
internal sealed class DraftHoldingOpener(
    CustomersDbContext db,
    IAdministrationModule administration,
    TimeProvider clock,
    ILogger<DraftHoldingOpener> logger)
{
    public async Task<Result<Guid>> OpenAsync(DraftHoldingOrigin origin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origin);

        // ── Idempotent on the origin ─────────────────────────────────────────
        //
        // A lead converts once and a request is won once, but both are reachable from a retried
        // request. Returning the existing holding is the honest answer to "this outcome was about
        // this line"; creating a second would have a back office open two subscriptions for one.
        var sameLine = db.ProductHoldings
            .IgnoreQueryFilters()
            .Where(h => h.TenantId == origin.TenantId && h.ProductLineId == origin.ProductLineId);

        // Built in C# rather than as a conditional inside the predicate: the two branches compare
        // different columns, and a ternary in an expression tree translates to SQL that scans both.
        var sameOrigin = origin.SourceLeadId is { } leadId
            ? sameLine.Where(h => h.SourceLeadId == leadId)
            : sameLine.Where(h => h.SourceOpportunityId == origin.SourceOpportunityId);

        var existing = await sameOrigin
            .Select(h => (Guid?)h.Id)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
            return Result.Ok(existing.Value);

        // IgnoreQueryFilters plus an explicit tenant, here and above: this runs from M13, and the
        // ambient tenant context belongs to that request rather than being something this module
        // established.
        var clientExists = await db.Clients
            .IgnoreQueryFilters()
            .AnyAsync(c => c.TenantId == origin.TenantId && c.Id == origin.ClientId, ct);

        if (!clientExists)
            return Result.Fail<Guid>(CustomerErrors.ClientNotFound);

        BeneficiaryPolicyDto? line;

        try
        {
            using var suppressed = new System.Transactions.TransactionScope(
                System.Transactions.TransactionScopeOption.Suppress,
                System.Transactions.TransactionScopeAsyncFlowOption.Enabled);

            line = await administration.GetBeneficiaryPolicyAsync(
                origin.TenantId, origin.ProductLineId, ct);

            suppressed.Complete();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "No draft holding opened for {Origin}: the product catalogue is unreachable",
                origin.Describe());

            return Result.Fail<Guid>(CustomerErrors.ProductLineUnavailable);
        }

        if (line is null)
            return Result.Fail<Guid>(CustomerErrors.ProductLineNotFound);

        if (!line.AllowMultipleHoldings)
        {
            var alreadyHeld = await db.ProductHoldings
                .IgnoreQueryFilters()
                .AnyAsync(h => h.TenantId == origin.TenantId
                            && h.ClientId == origin.ClientId
                            && h.ProductLineId == origin.ProductLineId
                            && h.Status != HoldingStatus.Closed
                            && h.Status != HoldingStatus.Rejected,
                          ct);

            if (alreadyHeld)
                return Result.Fail<Guid>(CustomerErrors.HoldingAlreadyHeld);
        }

        var holding = ProductHolding.Subscribe(
            tenantId:     origin.TenantId,
            clientId:     origin.ClientId,
            line:         line,
            // Empty: the lead carried an interest and the request an estimated amount, not the
            // terms of a contract. An agent fills those in before asking for a signature, and
            // inventing them here would produce a draft that looks reviewed and is not.
            specifics:    HoldingSpecifics.None,
            createdBy:    origin.OpenedByUserId,
            now:          clock.GetUtcNow(),
            sourceLeadId: origin.SourceLeadId,
            sourceOpportunityId: origin.SourceOpportunityId);

        db.ProductHoldings.Add(holding);
        await db.SaveChangesAsync(ct);

        return Result.Ok(holding.Id);
    }
}

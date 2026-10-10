namespace Sankore.Modules.Customers.Features.Holdings.CreateFromLead;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// BNF-B-24: a converted lead with a resolved product line leaves a draft holding behind.
/// </summary>
/// <remarks>
/// It does NOT go through <c>HoldingWriteContext</c>, and that is deliberate: the context checks
/// the caller's AGENCY perimeter through <c>ICurrentUser</c>, and this command runs on M13's
/// conversion path where the ambient user is the converting agent but the client was created
/// moments earlier by the same transaction chain. The perimeter question has already been answered
/// — the agent converted the lead — and re-asking it against a brand-new client is how a
/// legitimate conversion fails on a race.
/// </remarks>
internal sealed class CreateHoldingFromLeadHandler(
    CustomersDbContext db,
    IAdministrationModule administration,
    TimeProvider clock,
    ILogger<CreateHoldingFromLeadHandler> logger)
    : IRequestHandler<CreateHoldingFromLeadCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateHoldingFromLeadCommand cmd, CancellationToken ct)
    {
        // ── Idempotent on the lead ───────────────────────────────────────────
        //
        // A lead converts once, but the conversion is reachable from a retried request. Returning
        // the existing holding is the honest answer to "this lead was about this line"; creating a
        // second would have a back office open two subscriptions for one request.
        var existing = await db.ProductHoldings
            .IgnoreQueryFilters()
            .Where(h => h.TenantId == cmd.TenantId
                     && h.SourceLeadId == cmd.SourceLeadId
                     && h.ProductLineId == cmd.ProductLineId)
            .Select(h => (Guid?)h.Id)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
            return Result.Ok(existing.Value);

        // IgnoreQueryFilters plus an explicit tenant, here and above: this runs from M13's
        // conversion, and the ambient tenant context belongs to that request rather than being
        // something this module established.
        var client = await db.Clients
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == cmd.TenantId && c.Id == cmd.ClientId, ct);

        if (client is null)
            return Result.Fail<Guid>(CustomerErrors.ClientNotFound);

        BeneficiaryPolicyDto? line;
        try
        {
            using var suppressed = new System.Transactions.TransactionScope(
                System.Transactions.TransactionScopeOption.Suppress,
                System.Transactions.TransactionScopeAsyncFlowOption.Enabled);

            line = await administration.GetBeneficiaryPolicyAsync(
                cmd.TenantId, cmd.ProductLineId, ct);

            suppressed.Complete();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "No draft holding opened for lead {LeadId}: the product catalogue is unreachable",
                cmd.SourceLeadId);

            return Result.Fail<Guid>(CustomerErrors.ProductLineUnavailable);
        }

        if (line is null)
            return Result.Fail<Guid>(CustomerErrors.ProductLineNotFound);

        if (!line.AllowMultipleHoldings)
        {
            var alreadyHeld = await db.ProductHoldings
                .IgnoreQueryFilters()
                .AnyAsync(h => h.TenantId == cmd.TenantId
                            && h.ClientId == cmd.ClientId
                            && h.ProductLineId == cmd.ProductLineId
                            && h.Status != HoldingStatus.Closed
                            && h.Status != HoldingStatus.Rejected,
                          ct);

            if (alreadyHeld)
                return Result.Fail<Guid>(CustomerErrors.HoldingAlreadyHeld);
        }

        var holding = ProductHolding.Subscribe(
            tenantId:     cmd.TenantId,
            clientId:     cmd.ClientId,
            line:         line,
            // Empty: the lead carried an interest and a desired amount, not the terms of a
            // contract. An agent fills those in before asking for a signature, and inventing them
            // here would produce a draft that looks reviewed and is not.
            specifics:    HoldingSpecifics.None,
            createdBy:    cmd.ConvertedByUserId,
            now:          clock.GetUtcNow(),
            sourceLeadId: cmd.SourceLeadId);

        db.ProductHoldings.Add(holding);
        await db.SaveChangesAsync(ct);

        return Result.Ok(holding.Id);
    }
}

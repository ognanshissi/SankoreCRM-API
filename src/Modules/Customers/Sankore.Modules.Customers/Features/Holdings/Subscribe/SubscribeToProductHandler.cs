namespace Sankore.Modules.Customers.Features.Holdings.Subscribe;

using MediatR;
using Sankore.Modules.Administration.PublicApi;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Holdings.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class SubscribeToProductHandler(
    CustomersDbContext db,
    HoldingWriteContext context,
    DesignationSubjectValidator subjects,
    IInstitutionCurrency? institutionCurrency = null)
    : IRequestHandler<SubscribeToProductCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(SubscribeToProductCommand cmd, CancellationToken ct)
    {
        var client = await context.EnsureClientInScopeAsync(cmd.ClientId, ct);
        if (client.IsFailure)
            return Result.Fail<Guid>(client.Error!);

        var line = await context.ReadLineAsync(client.Value.TenantId, cmd.ProductLineId, ct);
        if (line.IsFailure)
            return Result.Fail<Guid>(line.Error!);

        // ── One live holding per line, when the line says so (BNF-B-22) ──────
        //
        // The check is HERE and not in the index, because whether the rule applies lives in M12's
        // catalogue and a partial index can only see this table. ux_product_holdings_live_per_line
        // is what covers the two requests arriving at the same instant; this is what gives the
        // agent a code they can act on, and what lets a second savings account through.
        if (!line.Value.AllowMultipleHoldings)
        {
            var alreadyHeld = await db.ProductHoldings.AnyAsync(
                h => h.ClientId == cmd.ClientId
                  && h.ProductLineId == cmd.ProductLineId
                  && h.Status != HoldingStatus.Closed
                  && h.Status != HoldingStatus.Rejected,
                ct);

            if (alreadyHeld)
                return Result.Fail<Guid>(CustomerErrors.HoldingAlreadyHeld);
        }

        var specifics = await HoldingMapping.ToSpecificsAsync(
            cmd.Specifics, client.Value.TenantId, stored: null, institutionCurrency, ct);
        if (specifics.IsFailure)
            return Result.Fail<Guid>(specifics.Error!);

        var now = context.Clock.GetUtcNow();

        var holding = ProductHolding.Subscribe(
            tenantId:     client.Value.TenantId,
            clientId:     cmd.ClientId,
            line:         line.Value,
            specifics:    specifics.Value,
            createdBy:    context.Actor,
            now:          now,
            sourceLeadId: cmd.SourceLeadId);

        // The clause may be submitted with the subscription, which is how a conversion carries the
        // designations the lead already collected (BNF-B-24). Validated by the aggregate against
        // the line's roles, so an invalid one refuses the whole subscription rather than leaving a
        // holding behind with no beneficiaries.
        if (cmd.Designations is { Count: > 0 })
        {
            var requests = HoldingMapping.ToRequests(cmd.Designations);
            if (requests.IsFailure)
                return Result.Fail<Guid>(requests.Error!);

            // The same subject check as the designation endpoint — this path accepts a clause too,
            // so leaving it out would make the subscription endpoint the way round the rule.
            var subjectsValid = await subjects.EnsureValidAsync(
                client.Value.TenantId, cmd.ClientId, requests.Value, ct);

            if (subjectsValid.IsFailure)
                return Result.Fail<Guid>(subjectsValid.Error!);

            var designated = holding.Designate(
                requests.Value, line.Value.AllowedRoles, context.Actor, now);

            if (designated.IsFailure)
                return Result.Fail<Guid>(designated.Error!);
        }

        db.ProductHoldings.Add(holding);
        await context.SaveAsync(ct);

        return Result.Ok(holding.Id);
    }
}

namespace Sankore.Modules.Customers.Features.Holdings.Transitions;

using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Holdings.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Infrastructure.Compliance;
using Sankore.Shared.Kernel;

/// <summary>
/// The five agent-driven transitions of a holding (BNF-B-14). Grouped in one file because each is
/// four lines over the same load-act-save shape, and splitting them into five folders would bury
/// the state machine they share rather than clarify it.
///
/// <para>
/// <c>ConfirmSync</c> and <c>RecordRejection</c> are deliberately NOT here: they are the back
/// office answering, which arrives as an integration event and not as a request from an agent.
/// They live on the aggregate and the Integration consumer calls them.
/// </para>
/// </summary>
internal sealed class RequestSignatureHandler(HoldingWriteContext context)
    : IRequestHandler<RequestSignatureCommand, Result>
{
    public async Task<Result> Handle(RequestSignatureCommand cmd, CancellationToken ct)
    {
        var loaded = await context.LoadAsync(cmd.HoldingId, ct);
        if (loaded.IsFailure)
            return Result.Fail(loaded.Error!);

        var holding = loaded.Value;

        var line = await context.ReadLineOfAsync(holding, ct);
        if (line.IsFailure)
            return Result.Fail(line.Error!);

        // Which required roles the clause does not satisfy, computed HERE because the policy is
        // M12's and the aggregate must not read another module.
        var missing = line.Value.AllowedRoles
            .Where(role => role.MinCount > 0
                        && holding.CurrentDesignations.Count(d =>
                               string.Equals(d.RoleCode, role.Code, StringComparison.OrdinalIgnoreCase))
                           < role.MinCount)
            .Select(role => role.Code)
            .ToList();

        var requested = holding.RequestSignature(missing, context.Clock.GetUtcNow());
        if (requested.IsFailure)
            return requested;

        await context.SaveAsync(ct);
        return Result.Ok();
    }
}

internal sealed class SignHoldingHandler(
    HoldingWriteContext context,
    InsuranceDistributionGuard distributionGuard)
    : IRequestHandler<SignHoldingCommand, Result>
{
    public async Task<Result> Handle(SignHoldingCommand cmd, CancellationToken ct)
    {
        var loaded = await context.LoadAsync(cmd.HoldingId, ct);
        if (loaded.IsFailure)
            return Result.Fail(loaded.Error!);

        // CIMA articles 503 and 510: on an insurance line, the person recording the client's
        // signature must hold a professional card. Checked HERE, at the signature, and not only
        // at the submission: the signature is the act of presenting the operation, which is what
        // the licence governs — a card that lapses between the two does not retroactively
        // un-present the contract. The submission re-checks anyway, for the reason BNF-B-16
        // re-checks the signature in the Integration consumer.
        //
        // Fails closed on an outage; see InsuranceDistributionGuard.
        var licensed = await distributionGuard.EnsureMayDistributeAsync(
            loaded.Value.TenantId, context.Actor, loaded.Value.ProductLineId, ct);

        if (licensed.IsFailure)
            return licensed;

        var now = context.Clock.GetUtcNow();

        var signed = loaded.Value.Sign(cmd.SignatureReference, cmd.SignedAt ?? now, now);
        if (signed.IsFailure)
            return signed;

        await context.SaveAsync(ct);
        return Result.Ok();
    }
}

internal sealed class SubmitHoldingHandler(
    HoldingWriteContext context,
    InsuranceDistributionGuard distributionGuard,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher)
    : IRequestHandler<SubmitHoldingCommand, Result>
{
    public async Task<Result> Handle(SubmitHoldingCommand cmd, CancellationToken ct)
    {
        var loaded = await context.LoadAsync(cmd.HoldingId, ct);
        if (loaded.IsFailure)
            return Result.Fail(loaded.Error!);

        var holding = loaded.Value;

        // Re-checked, although SignHoldingHandler already required it. This is the transition that
        // hands a contract to an insurer, and the two are separate acts that can be days apart —
        // a card withdrawn in between must stop the submission. Same argument the signature check
        // in Integration's consumer makes one step further down the chain.
        var licensed = await distributionGuard.EnsureMayDistributeAsync(
            holding.TenantId, context.Actor, holding.ProductLineId, ct);

        if (licensed.IsFailure)
            return licensed;

        var submitted = holding.SubmitForSync(context.Clock.GetUtcNow());
        if (submitted.IsFailure)
            return submitted;

        // Through the outbox, in the holding's own transaction: an event without its PendingSync
        // row would have a back office processing a subscription this system does not believe it
        // sent, and the row without the event would wait forever.
        await publisher.PublishAsync(
            new ProductSubscriptionRequestedEvent(
                TenantId:      holding.TenantId,
                ClientId:      holding.ClientId,
                HoldingId:     holding.Id,
                ProductLineId: holding.ProductLineId,
                ProductCode:   holding.ProductCode,
                ClauseVersion: holding.ClauseVersion,
                RequestedBy:   context.Actor),
            ct);

        await context.SaveAsync(ct);
        return Result.Ok();
    }
}

internal sealed class ReturnHoldingToDraftHandler(HoldingWriteContext context)
    : IRequestHandler<ReturnHoldingToDraftCommand, Result>
{
    public async Task<Result> Handle(ReturnHoldingToDraftCommand cmd, CancellationToken ct)
    {
        var loaded = await context.LoadAsync(cmd.HoldingId, ct);
        if (loaded.IsFailure)
            return Result.Fail(loaded.Error!);

        var returned = loaded.Value.ReturnToDraft(context.Clock.GetUtcNow());
        if (returned.IsFailure)
            return returned;

        await context.SaveAsync(ct);
        return Result.Ok();
    }
}

internal sealed class CloseHoldingHandler(HoldingWriteContext context)
    : IRequestHandler<CloseHoldingCommand, Result>
{
    public async Task<Result> Handle(CloseHoldingCommand cmd, CancellationToken ct)
    {
        var loaded = await context.LoadAsync(cmd.HoldingId, ct);
        if (loaded.IsFailure)
            return Result.Fail(loaded.Error!);

        var closed = loaded.Value.Close(cmd.Reason, context.Clock.GetUtcNow());
        if (closed.IsFailure)
            return closed;

        await context.SaveAsync(ct);
        return Result.Ok();
    }
}

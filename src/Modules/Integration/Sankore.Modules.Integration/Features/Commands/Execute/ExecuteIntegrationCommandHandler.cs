namespace Sankore.Modules.Integration.Features.Commands.Execute;

using System.Globalization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.References.GetReference;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// The one place a queued write actually leaves the platform (INT-05) — and the one place an
/// external identifier is recorded (INT-07).
///
/// <para>
/// Four things happen here, in this order, and the order is the correctness argument:
/// </para>
///
/// <list type="number">
/// <item><b>Claim.</b> <c>BeginSending</c> under the row's <c>xmin</c>, saved immediately, so a
///   second worker that picked the same command loses the race on the optimistic lock instead of
///   calling the CBS twice. The attempt is consumed by the claim and not by the failure: a call
///   that kills the worker must not be retried for ever.</item>
/// <item><b>Route.</b> A <see cref="IntegrationMode.Batch"/> connection — or a
///   <see cref="IntegrationMode.Relay"/> one carrying batch coordinates — has no endpoint to call;
///   its commands move to <c>Batched</c> and are closed later by an acknowledgement. Only an
///   <c>Api</c> connection reaches an adapter: a <c>Relay</c> one with no batch coordinates is
///   refused outright, because the order channel that would carry it is not built and calling
///   from this process is exactly what that mode forbids.</item>
/// <item><b>Call.</b> Dispatch on <see cref="CommandType"/> onto the port that serves it, with
///   the command's own idempotency key — so a timeout retried here is a no-op on the other
///   side rather than a second customer.</item>
/// <item><b>Record.</b> Map the <see cref="IntegrationResult"/> onto exactly one of
///   <c>Succeed</c> / <c>ScheduleRetry</c> / <c>Reject</c>, and on the success of a CREATION
///   write the <c>integration_reference</c> row in the SAME <c>SaveChangesAsync</c> as the status
///   move. A success recorded without its reference leaves a customer that exists in the CBS and
///   that SANKORE can no longer address — every later call would try to create it again.</item>
/// </list>
///
/// <para>
/// Nothing here throws on a refusal from the far end: an outage is a <c>Transient</c> result and
/// a refusal is a <c>Functional</c> one, and the family — not an HTTP status read in an adapter —
/// decides whether the command comes back.
/// </para>
/// </summary>
internal sealed class ExecuteIntegrationCommandHandler(
    IntegrationDbContext db,
    IntegrationAdapterResolver resolver,
    ReferenceLookup references,
    CommandPayloadProtector protector,
    CbsCustomerPayloadSource payloads,
    ICommandRetryPolicy retry,
    IBatchFileEnlister batchEnlister,
    [FromKeyedServices(nameof(IntegrationDbContext))] IEventPublisher publisher,
    TimeProvider clock,
    ILogger<ExecuteIntegrationCommandHandler> logger)
    : IRequestHandler<ExecuteIntegrationCommandCommand, Result<ExecuteIntegrationCommandResult>>
{
    public async Task<Result<ExecuteIntegrationCommandResult>> Handle(
        ExecuteIntegrationCommandCommand request, CancellationToken ct)
    {
        // AsTracking because this mutates, IgnoreQueryFilters plus an explicit predicate because
        // the dispatcher runs outside any HTTP context and the tenant being processed is the
        // argument, not whatever the ambient context happens to hold.
        var command = await db.Commands
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == request.TenantId && c.Id == request.CommandId, ct);

        if (command is null)
            return Result.Fail<ExecuteIntegrationCommandResult>(IntegrationErrors.CommandNotFound);

        // Not claimable: another worker took it, or a human cancelled it between the dispatcher's
        // scan and this call. Checked rather than left to the aggregate's exception because a race
        // is an expected outcome, not the programming error the transition table guards against —
        // and the dispatcher needs to carry on with the rest of its page.
        if (!command.CanTransitionTo(CommandStatus.Sending))
        {
            logger.LogDebug(
                "Command {CommandId} is {Status} and no longer claimable; nothing to do",
                command.Id, command.Status);

            return Ok(command);
        }

        command.BeginSending(clock);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Lost the claim on xmin. The other worker is calling the external system right now;
            // calling it too is the single worst thing this handler could do.
            logger.LogInformation(
                "Command {CommandId} was claimed concurrently; leaving it to the other worker",
                command.Id);

            return Result.Ok(new ExecuteIntegrationCommandResult(
                command.Id, nameof(CommandStatus.Sending), null, null));
        }

        var connection = await resolver.ResolveConnectionAsync(request.TenantId, command.ConnectionId, ct);
        if (connection.IsFailure)
            return await ApplyFailureAsync(command, connection, ct);

        // Does this write leave in a file? For Batch the answer is the mode's definition. For
        // Relay it is a statement about what has been BUILT: the agent's file carrier exists
        // (RelayFileTransport, and IntegrationFileTransportRouter already sends a Relay
        // connection's deposits to the agent), while its order channel does not — INT-26's
        // platform side is undelivered. So a Relay connection carrying file coordinates takes the
        // same path as a Batch one, and any other Relay connection is refused below.
        //
        // Reading the settings rather than the adapter's capability matrix keeps this decision
        // where it has always been: before an adapter is resolved, because a connection whose
        // writes leave in a file needs no adapter at all. The known imprecision is Amplitude Up
        // over relay — batch-capable settings, API carrier — which this would route to a file; it
        // is unreachable today because that adapter refuses every call, and the matrix is the
        // right authority for it once the order channel exists.
        var leavesInAFile = connection.Value.Mode == IntegrationMode.Batch
            || (connection.Value.Mode == IntegrationMode.Relay
                && connection.Value.Settings is BatchCapableSettings);

        if (leavesInAFile)
            return await EnlistInBatchAsync(command, connection.Value, ct);

        if (connection.Value.Mode == IntegrationMode.Relay)
        {
            // Fail CLOSED. Without this the dispatcher resolved an adapter and called the CBS
            // straight out of this process — for a connection whose whole point is that the CBS
            // is not reachable from here. On a Temenos row it is worse than a failed call: the Api
            // transport performs no egress validation, so a baseUrl pointing at a private address
            // would have the platform open a connection inside its OWN network, with the mode
            // making it look sanctioned.
            return await ApplyFailureAsync(
                command,
                IntegrationResult.Technical(
                    IntegrationErrors.RelayCommandChannelMissing,
                    $"Connection {connection.Value.Id} is in Relay mode and carries no batch "
                    + "coordinates, so this write would have to reach the agent as an order. That "
                    + "channel is not built (INT-26). Point the connection at the API directly, "
                    + "give it batch coordinates, or wait for the relay command channel — nothing "
                    + "is sent from here in the meantime."),
                ct);
        }

        var adapter = resolver.ResolveAdapter(connection.Value);
        if (adapter.IsFailure)
            return await ApplyFailureAsync(command, adapter, ct);

        var outcome = await CallAsync(command, adapter.Value, ct);

        return outcome.Result.IsSuccess
            ? await SucceedAsync(command, connection.Value, outcome, ct)
            : await ApplyFailureAsync(command, outcome.Result, ct);
    }

    // ── Routing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Dispatch on the command type. Exhaustive over <see cref="CommandType"/> on purpose: the
    /// enum exists so that adding an operation without teaching the dispatcher about it is a
    /// compile-time hole a reviewer sees, not a command that sits Pending for ever.
    /// </summary>
    private async Task<CallOutcome> CallAsync(
        IntegrationCommand command, ICbsAdapter adapter, CancellationToken ct)
    {
        var key = new IdempotencyKey(command.IdempotencyKey);

        return command.CommandType switch
        {
            CommandType.CreateCustomer => await CreateCustomerAsync(command, adapter, key, ct),
            CommandType.UpdateCustomer => await UpdateCustomerAsync(command, adapter, key, ct),
            CommandType.SetKycLevel => await SetKycLevelAsync(command, adapter, key, ct),
            CommandType.OpenAccount => await OpenAccountAsync(command, adapter, key, ct),
            CommandType.SubmitLoanApplication => await SubmitLoanAsync(command, adapter, key, ct),
            CommandType.DebitAccount => await DebitAsync(command, adapter, key, ct),
            CommandType.ReverseDebit => await ReverseDebitAsync(command, adapter, key, ct),
            CommandType.SubscribePolicy => await SubscribePolicyAsync(command, adapter, key, ct),
            CommandType.CancelPolicy => await CancelPolicyAsync(command, adapter, key, ct),
            CommandType.DeclareClaim => await DeclareClaimAsync(command, adapter, key, ct),

            _ => new CallOutcome(IntegrationResult.Technical(
                IntegrationErrors.CapabilityNotSupported,
                $"The dispatcher has no route for {command.CommandType}.")),
        };
    }

    private async Task<CallOutcome> CreateCustomerAsync(
        IntegrationCommand command, ICbsAdapter adapter, IdempotencyKey key, CancellationToken ct)
    {
        var payload = await CustomerPayloadAsync(command, ct);
        if (payload is null) return MissingPayload(command);

        var port = resolver.ResolvePort<ICbsCustomerPort>(adapter, IntegrationCapability.CreateCustomer);
        if (port.IsFailure) return new CallOutcome(port);

        var created = await port.Value.CreateCustomerAsync(payload, key, ct);

        return created.IsFailure
            ? new CallOutcome(created)
            : new CallOutcome(
                created,
                ExternalResponseRef: created.Value.Value,
                ReferenceEntityType: IntegrationEntityTypes.Customer,
                ReferenceExternalId: created.Value.Value);
    }

    private async Task<CallOutcome> UpdateCustomerAsync(
        IntegrationCommand command, ICbsAdapter adapter, IdempotencyKey key, CancellationToken ct)
    {
        var payload = await CustomerPayloadAsync(command, ct);
        if (payload is null) return MissingPayload(command);

        var customer = await ExternalCustomerAsync(command, ct);
        if (customer is null) return UnknownExternally(IntegrationEntityTypes.Customer, command.CrmId);

        var port = resolver.ResolvePort<ICbsCustomerPort>(adapter, IntegrationCapability.UpdateCustomer);
        if (port.IsFailure) return new CallOutcome(port);

        return new CallOutcome(
            await port.Value.UpdateCustomerAsync(customer.Value, payload, key, ct),
            ExternalResponseRef: customer.Value.Value);
    }

    private async Task<CallOutcome> SetKycLevelAsync(
        IntegrationCommand command, ICbsAdapter adapter, IdempotencyKey key, CancellationToken ct)
    {
        var payload = protector.Unprotect<SetKycLevelPayload>(command.PayloadEncrypted);
        if (payload is null) return MissingPayload(command);

        var customer = await ExternalCustomerAsync(command, ct);
        if (customer is null) return UnknownExternally(IntegrationEntityTypes.Customer, command.CrmId);

        var port = resolver.ResolvePort<ICbsCustomerPort>(adapter, IntegrationCapability.SetKycLevel);
        if (port.IsFailure) return new CallOutcome(port);

        return new CallOutcome(
            await port.Value.SetKycLevelAsync(customer.Value, payload.KycLevel, key, ct),
            ExternalResponseRef: customer.Value.Value);
    }

    private async Task<CallOutcome> OpenAccountAsync(
        IntegrationCommand command, ICbsAdapter adapter, IdempotencyKey key, CancellationToken ct)
    {
        var payload = protector.Unprotect<OpenAccountPayload>(command.PayloadEncrypted);
        if (payload is null) return MissingPayload(command);

        var customer = await ExternalCustomerAsync(command, ct);
        if (customer is null) return UnknownExternally(IntegrationEntityTypes.Customer, command.CrmId);

        var port = resolver.ResolvePort<ICbsAccountPort>(adapter, IntegrationCapability.OpenAccount);
        if (port.IsFailure) return new CallOutcome(port);

        var opened = await port.Value.OpenAccountAsync(customer.Value, payload.ProductCode, key, ct);

        return opened.IsFailure
            ? new CallOutcome(opened)
            : new CallOutcome(
                opened,
                ExternalResponseRef: opened.Value.Value,
                ReferenceEntityType: IntegrationEntityTypes.Account,
                ReferenceExternalId: opened.Value.Value,
                ExternalCustomerId: customer.Value.Value,
                ProductCode: payload.ProductCode);
    }

    private async Task<CallOutcome> SubmitLoanAsync(
        IntegrationCommand command, ICbsAdapter adapter, IdempotencyKey key, CancellationToken ct)
    {
        var payload = protector.Unprotect<SubmitLoanApplicationPayload>(command.PayloadEncrypted);
        if (payload is null) return MissingPayload(command);

        var customer = await ExternalCustomerAsync(command, ct);
        if (customer is null) return UnknownExternally(IntegrationEntityTypes.Customer, command.CrmId);

        var port = resolver.ResolvePort<ICbsLoanPort>(
            adapter, IntegrationCapability.SubmitLoanApplication);
        if (port.IsFailure) return new CallOutcome(port);

        var submitted = await port.Value.SubmitLoanApplicationAsync(
            new CbsLoanApplicationPayload(
                CrmCustomerId: command.CrmId,
                CustomerId: customer.Value,
                ProductCode: payload.ProductCode,
                Amount: payload.Amount,
                Currency: payload.Currency,
                TermMonths: payload.TermMonths,
                Purpose: payload.Purpose),
            key, ct);

        return submitted.IsFailure
            ? new CallOutcome(submitted)
            : new CallOutcome(
                submitted,
                ExternalResponseRef: submitted.Value.Value,
                ReferenceEntityType: IntegrationEntityTypes.Loan,
                ReferenceExternalId: submitted.Value.Value);
    }

    private async Task<CallOutcome> DebitAsync(
        IntegrationCommand command, ICbsAdapter adapter, IdempotencyKey key, CancellationToken ct)
    {
        var payload = protector.Unprotect<DebitAccountPayload>(command.PayloadEncrypted);
        if (payload is null) return MissingPayload(command);

        var port = resolver.ResolvePort<ICbsAccountPort>(adapter, IntegrationCapability.DebitAccount);
        if (port.IsFailure) return new CallOutcome(port);

        var receipt = await port.Value.DebitAccountAsync(
            new ExternalId(payload.AccountRef), payload.Amount, payload.Currency, payload.Label, key, ct);

        // No reference row: a debit creates no entity SANKORE addresses later. Its receipt
        // reference is what the insurance statement reconciles against, and that is a command
        // column, not a reference.
        return new CallOutcome(receipt, ExternalResponseRef: receipt.IsSuccess ? receipt.Value.Reference : null);
    }

    private async Task<CallOutcome> ReverseDebitAsync(
        IntegrationCommand command, ICbsAdapter adapter, IdempotencyKey key, CancellationToken ct)
    {
        var payload = protector.Unprotect<ReverseDebitPayload>(command.PayloadEncrypted);
        if (payload is null) return MissingPayload(command);

        var port = resolver.ResolvePort<ICbsAccountPort>(adapter, IntegrationCapability.ReverseDebit);
        if (port.IsFailure) return new CallOutcome(port);

        var receipt = await port.Value.ReverseDebitAsync(
            new ExternalId(payload.AccountRef), payload.OriginalReference, key, ct);

        return new CallOutcome(receipt, ExternalResponseRef: receipt.IsSuccess ? receipt.Value.Reference : null);
    }

    private async Task<CallOutcome> SubscribePolicyAsync(
        IntegrationCommand command, ICbsAdapter adapter, IdempotencyKey key, CancellationToken ct)
    {
        var payload = protector.Unprotect<InsurancePolicyPayload>(command.PayloadEncrypted);
        if (payload is null) return MissingPayload(command);

        var port = resolver.ResolvePort<IInsurancePolicyPort>(
            adapter, IntegrationCapability.SubscribePolicy);
        if (port.IsFailure) return new CallOutcome(port);

        var subscribed = await port.Value.SubscribeAsync(payload, key, ct);

        return subscribed.IsFailure
            ? new CallOutcome(subscribed)
            : new CallOutcome(
                subscribed,
                ExternalResponseRef: subscribed.Value.Value,
                ReferenceEntityType: IntegrationEntityTypes.Policy,
                ReferenceExternalId: subscribed.Value.Value);
    }

    private async Task<CallOutcome> CancelPolicyAsync(
        IntegrationCommand command, ICbsAdapter adapter, IdempotencyKey key, CancellationToken ct)
    {
        var payload = protector.Unprotect<CancelPolicyPayload>(command.PayloadEncrypted);
        if (payload is null) return MissingPayload(command);

        var port = resolver.ResolvePort<IInsurancePolicyPort>(
            adapter, IntegrationCapability.CancelPolicy);
        if (port.IsFailure) return new CallOutcome(port);

        return new CallOutcome(
            await port.Value.CancelAsync(new ExternalId(payload.ExternalPolicyId), payload.Reason, key, ct),
            ExternalResponseRef: payload.ExternalPolicyId);
    }

    private async Task<CallOutcome> DeclareClaimAsync(
        IntegrationCommand command, ICbsAdapter adapter, IdempotencyKey key, CancellationToken ct)
    {
        var payload = protector.Unprotect<InsuranceClaimPayload>(command.PayloadEncrypted);
        if (payload is null) return MissingPayload(command);

        var port = resolver.ResolvePort<IInsuranceClaimPort>(adapter, IntegrationCapability.DeclareClaim);
        if (port.IsFailure) return new CallOutcome(port);

        var declared = await port.Value.DeclareAsync(payload, key, ct);

        return declared.IsFailure
            ? new CallOutcome(declared)
            : new CallOutcome(
                declared,
                ExternalResponseRef: declared.Value.Value,
                ReferenceEntityType: IntegrationEntityTypes.Claim,
                ReferenceExternalId: declared.Value.Value);
    }

    // ── Recording the outcome ───────────────────────────────────────────────

    /// <summary>
    /// The success path, and INT-07's first criterion: the reference row and the move to
    /// <c>Succeeded</c> share ONE <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>.
    /// A failed save therefore leaves neither — no reference pointing at a command that never
    /// succeeded, and no success nobody can address.
    /// </summary>
    private async Task<Result<ExecuteIntegrationCommandResult>> SucceedAsync(
        IntegrationCommand command,
        IntegrationConnection connection,
        CallOutcome outcome,
        CancellationToken ct)
    {
        if (outcome.ReferenceEntityType is { } entityType && outcome.ReferenceExternalId is { } externalId)
        {
            var guard = await GuardReferenceAsync(command, connection, entityType, externalId, ct);

            // A conflicting reference is a Functional refusal and not an exception: the external
            // system gave us an id that contradicts one we already hold, which a human has to
            // look at. Letting the unique index raise it instead would fail the save and lose the
            // command's outcome along with it.
            if (guard.IsFailure)
                return await ApplyFailureAsync(command, guard, ct);

            if (guard.Value == ReferenceAction.Insert)
                db.References.Add(IntegrationReference.Create(
                    tenantId: command.TenantId,
                    connectionId: connection.Id,
                    kind: connection.Kind,
                    entityType: entityType,
                    crmId: command.CrmId,
                    externalId: externalId,
                    clock: clock));
        }

        command.Succeed(outcome.ExternalResponseRef, clock);

        await PublishSuccessAsync(command, connection, outcome, ct);

        // One save: the reference, the status, and the outbox row the events were written to.
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Command {CommandId} ({CommandType}) succeeded on connection {ConnectionId}",
            command.Id, command.CommandType, connection.Id);

        return Ok(command);
    }

    /// <summary>
    /// Whether to insert the reference, skip it, or refuse — the in-handler half of INT-07's
    /// "unique in both directions, per connection".
    ///
    /// <para>
    /// The index is the guarantee; this is what turns a violation into an outcome the command can
    /// carry. Both directions are checked because both can be violated by a correct-looking call:
    /// a retry whose first attempt actually succeeded answers a second external id for the same
    /// CRM entity, and a mis-mapped CRM id answers an external id another entity already holds.
    /// </para>
    /// </summary>
    private async Task<IntegrationResult<ReferenceAction>> GuardReferenceAsync(
        IntegrationCommand command,
        IntegrationConnection connection,
        string entityType,
        string externalId,
        CancellationToken ct)
    {
        var existing = await db.References
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == command.TenantId
                        && r.ConnectionId == connection.Id
                        && r.EntityType == entityType
                        && (r.CrmId == command.CrmId || r.ExternalId == externalId))
            .ToListAsync(ct);

        // Already recorded, identically: a replayed success. Idempotent, not a conflict.
        if (existing.Any(r => r.CrmId == command.CrmId && r.ExternalId == externalId))
            return IntegrationResult.Ok(ReferenceAction.Skip);

        if (existing.FirstOrDefault(r => r.CrmId == command.CrmId) is { } sameCrm)
            return IntegrationResult.Functional<ReferenceAction>(
                IntegrationErrors.Duplicate,
                $"{entityType} {command.CrmId} is already mapped to {sameCrm.ExternalId} on this "
                + $"connection; the external system answered {externalId}.");

        if (existing.FirstOrDefault(r => r.ExternalId == externalId) is { } sameExternal)
            return IntegrationResult.Functional<ReferenceAction>(
                IntegrationErrors.Duplicate,
                $"External {entityType} {externalId} is already mapped to CRM "
                + $"{sameExternal.CrmId} on this connection.");

        return IntegrationResult.Ok(ReferenceAction.Insert);
    }

    /// <summary>
    /// The events INT-14's onboarding chain hangs off. Through the outbox, so they are committed
    /// by the same save as the status — an event published outside it would announce a creation a
    /// failed transaction rolled back.
    /// </summary>
    private async Task PublishSuccessAsync(
        IntegrationCommand command,
        IntegrationConnection connection,
        CallOutcome outcome,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        switch (command.CommandType)
        {
            case CommandType.CreateCustomer when outcome.ReferenceExternalId is { } externalCustomerId:
                await publisher.PublishAsync(new CbsCustomerCreatedEvent(
                    TenantId: command.TenantId,
                    CrmCustomerId: command.CrmId,
                    ConnectionId: connection.Id,
                    ExternalCustomerId: externalCustomerId,
                    CreatedAt: now), ct);
                break;

            case CommandType.OpenAccount
                when outcome.ReferenceExternalId is { } externalAccountId
                     && outcome.ExternalCustomerId is { } externalCustomer:
                await publisher.PublishAsync(new CbsAccountOpenedEvent(
                    TenantId: command.TenantId,
                    CrmCustomerId: command.CrmId,
                    ConnectionId: connection.Id,
                    ExternalCustomerId: externalCustomer,
                    ExternalAccountId: externalAccountId,
                    ProductCode: outcome.ProductCode ?? string.Empty,
                    OpenedAt: now), ct);
                break;

            default:
                // Every other success is between this module and the external system. The policy
                // and claim events of ASS-04/ASS-09 carry insurer-side detail this handler does
                // not have (a policy number, a status) and are published by the slices that read
                // it back.
                break;
        }
    }

    /// <summary>
    /// A batch connection's commands leave through a file. The move is
    /// <c>Sending → Batched</c>, which is why the claim above happens for a batch connection too:
    /// the transition table has no edge from <c>Pending</c> to <c>Batched</c>, and the claim is
    /// also what stops two workers enlisting the same command in two different files.
    /// </summary>
    private async Task<Result<ExecuteIntegrationCommandResult>> EnlistInBatchAsync(
        IntegrationCommand command, IntegrationConnection connection, CancellationToken ct)
    {
        var enlisted = await batchEnlister.EnlistAsync(command, connection, ct);
        if (enlisted.IsFailure)
            return await ApplyFailureAsync(command, enlisted, ct);

        command.MarkBatched(enlisted.Value, clock);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Command {CommandId} joined outbound batch file {BatchFileId}", command.Id, enlisted.Value);

        return Ok(command);
    }

    /// <summary>
    /// The failure path, for every kind of failure: resolution, payload, call, reference
    /// conflict. One method so the retry budget is applied once and the rejection event cannot be
    /// forgotten on one of the five paths that can fail.
    /// </summary>
    private async Task<Result<ExecuteIntegrationCommandResult>> ApplyFailureAsync(
        IntegrationCommand command, IntegrationResult failure, CancellationToken ct)
    {
        var code = failure.Code!;
        var family = failure.Family ?? ErrorFamily.Technical;

        // A scheduled wait is handled BEFORE the retry budget is consulted, because it is not a
        // failure of anything: nothing was attempted. See IntegrationCommand.DeferUntil for why
        // counting it as an attempt does not merely delay the write but loses it.
        if (code == IntegrationErrors.BatchCycleNotDue
            && TryReadDueAt(failure.Detail, out var dueAt, out var reason)
            && dueAt > clock.GetUtcNow())
        {
            command.DeferUntil(dueAt, code, reason, clock);
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Command {CommandId} waits for the batch cut-off at {DueAt:u}; its attempt count "
                + "stays at {Attempts}/{Max}.",
                command.Id, dueAt, command.Attempts, retry.MaxAttempts);

            return Ok(command);
        }

        // Retried only when the cause can pass AND the budget allows it. A Functional refusal is
        // never retried — the external system answered and said no — and a Technical one cannot
        // be fixed by trying again.
        if (family == ErrorFamily.Transient && command.Attempts < retry.MaxAttempts)
        {
            var nextAttemptAt = retry.NextAttemptAt(command.Attempts);
            command.ScheduleRetry(nextAttemptAt, code, failure.Detail, clock);
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Command {CommandId} failed transiently ({Code}); attempt {Attempts}/{Max}, due {NextAttemptAt}",
                command.Id, code, command.Attempts, retry.MaxAttempts, nextAttemptAt);

            return Ok(command);
        }

        command.Reject(family, code, failure.Detail, clock);

        // Nobody reads a rejection queue unprompted, which is the whole reason this is an event
        // and not a log line: M08 turns it into an administrator's notification.
        await publisher.PublishAsync(new IntegrationCommandRejectedEvent(
            TenantId: command.TenantId,
            CommandId: command.Id,
            ConnectionId: command.ConnectionId,
            CommandType: command.CommandType.ToString(),
            EntityType: command.EntityType,
            CrmId: command.CrmId,
            ErrorFamily: family.ToString(),
            ErrorCode: code,
            Detail: failure.Detail,
            Attempts: command.Attempts,
            RejectedAt: clock.GetUtcNow()), ct);

        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "Command {CommandId} rejected after {Attempts} attempt(s): {Family}/{Code}",
            command.Id, command.Attempts, family, code);

        return Ok(command);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the instant the batch generator prefixes to its detail (<c>"{instant:O}|{reason}"</c>)
    /// and hands back the human half separately, so the stored message carries no machine prefix.
    ///
    /// <para>
    /// Parsed rather than recomputed: the cut-off hour lives in the connection's settings, which
    /// this handler never binds, and deriving it from a second source would be a second place to
    /// get it wrong. A malformed prefix — or an instant already past — falls through to the
    /// ordinary retry budget on purpose: that spends attempts and ends in a visible rejection,
    /// which is the right outcome for a bug here, whereas deferring to a moment that has already
    /// passed would spin the dispatcher.
    /// </para>
    /// </summary>
    private static bool TryReadDueAt(string? detail, out DateTimeOffset dueAt, out string? reason)
    {
        dueAt = default;
        reason = detail;

        if (detail is null) return false;

        var separator = detail.IndexOf('|', StringComparison.Ordinal);
        if (separator <= 0) return false;

        if (!DateTimeOffset.TryParseExact(
                detail[..separator], "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out dueAt))
            return false;

        reason = detail[(separator + 1)..];
        return true;
    }

    /// <summary>
    /// The external customer id this command's entity maps to, or <c>null</c>.
    ///
    /// <para>
    /// Always read through <see cref="ReferenceLookup"/> and never from the command: an operation
    /// on an existing customer needs the id the CBS assigned, and only the reference table holds
    /// it.
    /// </para>
    /// </summary>
    private async Task<ExternalId?> ExternalCustomerAsync(IntegrationCommand command, CancellationToken ct)
    {
        var externalId = await references.GetExternalIdAsync(
            command.TenantId, command.ConnectionId, IntegrationEntityTypes.Customer, command.CrmId, ct);

        return externalId is null ? null : new ExternalId(externalId);
    }

    /// <summary>
    /// The customer payload to send: RE-DERIVED from current CRM state, falling back to the one
    /// recorded when the command was queued.
    ///
    /// <para>
    /// This is the system re-derivation path, and the only caller of
    /// <c>IntegrationCommand.ReplacePayload</c>. It exists because the CRM is the source of
    /// truth: a command rejected for a wrong value is fixed by correcting the client record in
    /// M01 and replaying, and the replay has to pick the correction up. The alternative — letting
    /// an operator hand a corrected payload to the replay endpoint — would be a write authorised
    /// by one permission whose content the audit row cannot show, since INT-08 keeps payload
    /// values out of it by design.
    /// </para>
    ///
    /// <para>
    /// The re-derived payload is written back onto the command, so the row and the field-name
    /// list describe what was actually sent rather than what was first queued. When M01 no longer
    /// knows the customer — purged, or merged away — the stored payload stands: it is still the
    /// write that was decided, and inventing a rejection out of a read that happened later would
    /// discard a legitimate order.
    /// </para>
    /// </summary>
    private async Task<CbsCustomerPayload?> CustomerPayloadAsync(
        IntegrationCommand command, CancellationToken ct)
    {
        var stored = protector.Unprotect<CbsCustomerPayload>(command.PayloadEncrypted);

        var current = await payloads.BuildAsync(command.TenantId, command.CrmId, ct);
        if (current is null) return stored;

        var protected_ = protector.Protect(current);
        command.ReplacePayload(protected_.Ciphertext, protected_.FieldNames);

        return current;
    }

    /// <summary>
    /// No reference yet for an operation that needs one.
    ///
    /// <para>
    /// <c>Functional</c> and not <c>Transient</c>, deliberately. The onboarding chain is
    /// event-driven — INT-14 requests the KYC level and the account only once the creation has
    /// succeeded — so a missing reference is not an ordering race to wait out; it means the
    /// creation was rejected or never requested. The command belongs in the rejection queue,
    /// where a replay is one click away once the customer exists.
    /// </para>
    /// </summary>
    private static CallOutcome UnknownExternally(string entityType, Guid crmId)
        => new(IntegrationResult.Functional(
            IntegrationErrors.ExternalEntityNotFound,
            $"No {entityType} reference exists for CRM {crmId} on this connection; the creation "
            + "has not succeeded yet."));

    /// <summary>
    /// A payload that is absent, undecryptable, or no longer shaped like the operation. Technical
    /// because it is OUR data that is wrong, and the detail names the command type rather than
    /// quoting anything from the payload.
    /// </summary>
    private static CallOutcome MissingPayload(IntegrationCommand command)
        => new(IntegrationResult.Technical(
            IntegrationErrors.PayloadInvalid,
            $"The stored payload of this {command.CommandType} command is missing or no longer "
            + "readable as that operation's payload."));

    private static Result<ExecuteIntegrationCommandResult> Ok(IntegrationCommand command)
        => Result.Ok(new ExecuteIntegrationCommandResult(
            command.Id,
            command.Status.ToString(),
            command.LastErrorFamily?.ToString(),
            // The stored message is "CODE: detail"; the code alone is what a caller branches on.
            command.LastErrorMessage?.Split(':', 2)[0]));

    private enum ReferenceAction
    {
        Insert,
        Skip,
    }

    /// <summary>
    /// What one call to an external system produced. <see cref="ReferenceEntityType"/> and
    /// <see cref="ReferenceExternalId"/> are both set exactly when the call CREATED something
    /// SANKORE must be able to address later — which is what makes a reference row owed.
    /// </summary>
    private sealed record CallOutcome(
        IntegrationResult Result,
        string? ExternalResponseRef = null,
        string? ReferenceEntityType = null,
        string? ReferenceExternalId = null,
        string? ExternalCustomerId = null,
        string? ProductCode = null);
}

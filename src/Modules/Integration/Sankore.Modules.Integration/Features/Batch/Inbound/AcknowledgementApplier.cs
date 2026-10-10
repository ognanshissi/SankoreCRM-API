namespace Sankore.Modules.Integration.Features.Batch.Inbound;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// One acknowledgement line, applied — INT-25's criterion 2.
///
/// <para>
/// <b>The move to <c>Succeeded</c> and the <c>integration_reference</c> row share ONE
/// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>, so a failed save leaves
/// neither.</b> That is INT-07's requirement and it is the same single save
/// <c>ExecuteIntegrationCommandHandler.SucceedAsync</c> performs on the live path. A success
/// recorded without its reference leaves an entity that exists in the external system and that
/// SANKORE can no longer address: every later call would try to create it again, and the customer
/// would end up with two accounts at their bank.
/// </para>
///
/// <para>
/// <b>One save per LINE, not one per file.</b> An acknowledgement file is a day of closures for a
/// tenant, and a single transaction over the whole file would make one unique-index violation on
/// line 40 undo the thirty-nine commands that closed correctly — which a re-sent file cannot
/// repair, because the far end has no reason to send yesterday's acknowledgements twice. Per line
/// also makes the file re-readable: a poll interrupted halfway has closed exactly the lines it
/// committed, and a second read of the same file reports the already-closed ones and applies the
/// rest.
/// </para>
///
/// <para>
/// <b>Nothing here ever decides an outcome the file did not state.</b> A line that cannot be
/// understood leaves its command <c>Batched</c> — overdue, and therefore alerted by criterion 3 —
/// rather than being closed on a guess. Closing a command wrongly means telling a customer their
/// account exists when it does not.
/// </para>
/// </summary>
internal static class AcknowledgementApplier
{
    /// <summary>
    /// Applies one record. Returns what it closed, or the line report explaining why it closed
    /// nothing. Never throws for a line's own sake.
    /// </summary>
    public static async Task<AckLineOutcome> ApplyAsync(
        IntegrationDbContext db,
        TimeProvider clock,
        Guid tenantId,
        IntegrationConnection connection,
        string fileName,
        InboundBatchRecord record,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(record);

        var rawCommandId = record.Field(InboundBatchFileFormat.Ack.CommandId);

        if (!Guid.TryParse(rawCommandId, out var commandId) || commandId == Guid.Empty)
            return Report(fileName, record, InboundBatchCodes.LineMalformed,
                $"'{rawCommandId}' is not a command identifier.");

        var outcome = record.Field(InboundBatchFileFormat.Ack.Outcome)?.ToUpperInvariant();

        if (outcome is not (InboundBatchFileFormat.OutcomeSucceeded or InboundBatchFileFormat.OutcomeRejected))
            return Report(fileName, record, InboundBatchCodes.LineMalformed,
                $"'{record.Field(InboundBatchFileFormat.Ack.Outcome)}' is not an outcome "
                + $"({InboundBatchFileFormat.OutcomeSucceeded} or "
                + $"{InboundBatchFileFormat.OutcomeRejected}).");

        // IgnoreQueryFilters paired with an explicit tenant predicate, as every background path in
        // this repository does: a job runs outside any HTTP request and the ambient tenant is not
        // necessarily the one being served. The CONNECTION predicate is the second half of the
        // same guard and it is not cosmetic — the file was found in one connection's inbound
        // directory, so it may only close commands that left through that connection. Without it,
        // one IMF's SFTP server could close another's commands by naming their identifiers.
        var command = await db.Commands
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                c => c.Id == commandId
                  && c.TenantId == tenantId
                  && c.ConnectionId == connection.Id, ct);

        if (command is null)
            return Report(fileName, record, InboundBatchCodes.CommandUnknown,
                $"Command {commandId} is not a command of this connection.");

        // Idempotency, and the whole reason a re-read of a file is harmless. The transition table
        // allows Batched → Succeeded | Rejected and nothing else, so a command already closed
        // would make IntegrationCommand.MoveTo throw — a replayed file would fail the job rather
        // than be the no-op that at-least-once delivery requires.
        if (command.Status != CommandStatus.Batched)
            return Report(fileName, record, InboundBatchCodes.CommandNotAwaitingAck,
                $"Command {commandId} is {command.Status}, not Batched.");

        return outcome == InboundBatchFileFormat.OutcomeSucceeded
            ? await SucceedAsync(db, clock, connection, fileName, record, command, ct)
            : await RejectAsync(db, clock, fileName, record, command, ct);
    }

    /// <summary>
    /// The success path: the reference row and the status, in one save.
    /// </summary>
    private static async Task<AckLineOutcome> SucceedAsync(
        IntegrationDbContext db,
        TimeProvider clock,
        IntegrationConnection connection,
        string fileName,
        InboundBatchRecord record,
        IntegrationCommand command,
        CancellationToken ct)
    {
        var externalId = record.Field(InboundBatchFileFormat.Ack.ExternalId);

        // A success with no identifier is refused rather than recorded as a bare success. The
        // identifier IS what the acknowledgement delivers: without it the command would be
        // Succeeded and unaddressable, which is precisely the state criterion 2's single
        // transaction exists to make impossible.
        if (externalId is null)
            return Report(fileName, record, InboundBatchCodes.ExternalIdMissing,
                $"Command {command.Id} was acknowledged as successful with no external identifier.");

        var entityType = ReferenceEntityTypeFor(command.CommandType);

        if (entityType is not null)
        {
            var guard = await GuardReferenceAsync(db, command, connection, entityType, externalId, ct);

            if (guard.Conflict is { } conflict)
                return Report(fileName, record, InboundBatchCodes.ReferenceConflict, conflict);

            if (guard.Insert)
                db.References.Add(IntegrationReference.Create(
                    tenantId: command.TenantId,
                    connectionId: connection.Id,
                    kind: connection.Kind,
                    entityType: entityType,
                    crmId: command.CrmId,
                    externalId: externalId,
                    clock: clock));
        }

        command.Succeed(externalId, clock);

        return await CommitAsync(db, fileName, record, command, CommandStatus.Succeeded, ct);
    }

    /// <summary>
    /// The refusal path.
    ///
    /// <para>
    /// <b><see cref="ErrorFamily.Functional"/>, never <see cref="ErrorFamily.Transient"/>.</b> The
    /// external system answered and said no — that is the definition of a functional refusal in
    /// INT-02, and the family decides retry behaviour. Recording a batch refusal as transient
    /// would have the dispatcher re-send a write the CBS has already rejected on its own rules,
    /// for as long as the attempt budget lasts. The code and detail are the far end's own, carried
    /// through verbatim so the rejection queue shows what the CBS said rather than our paraphrase.
    /// </para>
    /// </summary>
    private static async Task<AckLineOutcome> RejectAsync(
        IntegrationDbContext db,
        TimeProvider clock,
        string fileName,
        InboundBatchRecord record,
        IntegrationCommand command,
        CancellationToken ct)
    {
        var reasonCode = record.Field(InboundBatchFileFormat.Ack.ReasonCode);

        // A refusal with no reason is refused in turn. A command rejected with nothing to show an
        // administrator is a command nobody can replay, and the overdue alert of criterion 3 is a
        // better outcome than a dead end in the rejection queue.
        if (reasonCode is null)
            return Report(fileName, record, InboundBatchCodes.ReasonMissing,
                $"Command {command.Id} was acknowledged as refused with no reason code.");

        command.Reject(
            ErrorFamily.Functional,
            reasonCode,
            record.Field(InboundBatchFileFormat.Ack.ReasonDetail),
            clock);

        return await CommitAsync(db, fileName, record, command, CommandStatus.Rejected, ct);
    }

    /// <summary>
    /// The one save, and the one place a failed save is turned back into a reported line.
    ///
    /// <para>
    /// <b>The change tracker is cleared on failure.</b> The command's modification and the
    /// reference's insert are still pending after a rejected save; left there they would be
    /// replayed by the NEXT line's save and fail that one too, so one bad line would take the
    /// remainder of the file with it. Same remedy, and the same reason, as
    /// <c>KycLimitWatchJob.RaiseAsync</c>.
    /// </para>
    ///
    /// <para>
    /// <see cref="DbUpdateException"/> and nothing broader: a unique-index violation on
    /// <c>ux_integration_reference_external</c> and a concurrency conflict on the command's
    /// <c>xmin</c> are both outcomes of this line, while a broken connection to the store is not
    /// something the next line can survive either and must fail the job.
    /// </para>
    /// </summary>
    private static async Task<AckLineOutcome> CommitAsync(
        IntegrationDbContext db,
        string fileName,
        InboundBatchRecord record,
        IntegrationCommand command,
        CommandStatus closed,
        CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);

            return new AckLineOutcome(closed, null);
        }
        catch (DbUpdateException ex)
        {
            db.ChangeTracker.Clear();

            return Report(fileName, record, IntegrationErrors.ConcurrencyConflict,
                $"Command {command.Id} could not be closed: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// Whether to insert the reference, skip it, or refuse the line.
    ///
    /// <para>
    /// The in-handler half of INT-07's "unique in both directions, per connection" — the index is
    /// the guarantee, this is what turns a violation into a reported line instead of a save that
    /// takes the command's closure down with it. Both directions are checked because a re-sent
    /// acknowledgement file can violate either: the same CRM entity acknowledged with a second
    /// external id, or one external id acknowledged against a second CRM entity.
    /// </para>
    ///
    /// <para>
    /// An identical reference is a SKIP and not a conflict: that is a replayed file, and the
    /// command is still closed.
    /// </para>
    /// </summary>
    private static async Task<ReferenceDecision> GuardReferenceAsync(
        IntegrationDbContext db,
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

        if (existing.Any(r => r.CrmId == command.CrmId && r.ExternalId == externalId))
            return new ReferenceDecision(Insert: false, Conflict: null);

        if (existing.FirstOrDefault(r => r.CrmId == command.CrmId) is { } sameCrm)
            return new ReferenceDecision(false,
                $"{entityType} {command.CrmId} is already mapped to {sameCrm.ExternalId} on this "
                + $"connection; the acknowledgement names {externalId}.");

        if (existing.FirstOrDefault(r => r.ExternalId == externalId) is { } sameExternal)
            return new ReferenceDecision(false,
                $"External {entityType} {externalId} is already mapped to {sameExternal.CrmId} on "
                + $"this connection; the acknowledgement names {command.CrmId}.");

        return new ReferenceDecision(Insert: true, Conflict: null);
    }

    /// <summary>
    /// Which entity an acknowledged command creates a reference for, or null when it creates none.
    ///
    /// <para>
    /// <b>It mirrors <c>ExecuteIntegrationCommandHandler</c> exactly</b>, and the two must keep
    /// agreeing: the same command type reaching a CBS by file and by call must produce the same
    /// reference row, or a tenant that switches from batch to API would hold two different
    /// bridges for one customer. Only the five creating operations produce one — an update, a KYC
    /// change, a cancellation and a debit act on an entity that is already referenced, and a
    /// debit creates no addressable entity at all.
    /// </para>
    /// </summary>
    internal static string? ReferenceEntityTypeFor(CommandType type) => type switch
    {
        CommandType.CreateCustomer => IntegrationEntityTypes.Customer,
        CommandType.OpenAccount => IntegrationEntityTypes.Account,
        CommandType.SubmitLoanApplication => IntegrationEntityTypes.Loan,
        CommandType.SubscribePolicy => IntegrationEntityTypes.Policy,
        CommandType.DeclareClaim => IntegrationEntityTypes.Claim,
        _ => null,
    };

    private static AckLineOutcome Report(
        string fileName, InboundBatchRecord record, string code, string detail)
        => new(null, new InboundLineReport(fileName, record.FileLine, code, detail));

    private sealed record ReferenceDecision(bool Insert, string? Conflict);
}

/// <summary>
/// What one acknowledgement line did: the status it moved its command to, or the report saying it
/// moved nothing. Exactly one of the two is set.
/// </summary>
internal sealed record AckLineOutcome(CommandStatus? Closed, InboundLineReport? Report);

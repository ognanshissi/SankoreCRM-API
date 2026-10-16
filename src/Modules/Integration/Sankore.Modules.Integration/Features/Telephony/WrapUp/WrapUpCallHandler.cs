namespace Sankore.Modules.Integration.Features.Telephony.WrapUp;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// Records the agent's qualification of a call, and tells M13 about it (TEL-B-11).
///
/// <para>
/// <b>The outcome is validated against the tenant's own referential and not against an enum.</b>
/// Two of its columns carry behaviour — one stops a lead's SLA clock, another makes a callback
/// date mandatory — so accepting an arbitrary string would let a caller invent an outcome with
/// no behaviour at all, and the call would be qualified in a way nothing downstream recognises.
/// An INACTIVE code is refused like an unknown one: deactivating an outcome is how an institution
/// retires it, and a screen holding a stale list must not be able to keep using it.
/// </para>
///
/// <para>
/// <b>Who may wrap up: the agent the call is attributed to, or nobody-is-attributed.</b> The same
/// rule as the manual attachment next door and for the same reason — a conversation is qualified
/// by whoever had it. The refusal is folded into "not found", because a 403 would confirm that a
/// colleague spoke to somebody.
/// </para>
///
/// <para>
/// <b>The event goes through the OUTBOX, in this transaction.</b> The activity M13 writes and the
/// wrap-up that justifies it must commit together: an event published outside would announce a
/// qualification that then rolled back, and M13 would carry a call activity for a call nobody
/// qualified.
/// </para>
/// </summary>
internal sealed class WrapUpCallHandler(
    IntegrationDbContext db,
    [FromKeyedServices(Infrastructure.Crypto.IntegrationFieldProtection.Key)] IFieldEncryptor encryptor,
    [FromKeyedServices(nameof(IntegrationDbContext))] IEventPublisher publisher,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<WrapUpCallHandler> logger)
    : IRequestHandler<WrapUpCallCommand, Result>
{
    public async Task<Result> Handle(WrapUpCallCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        var call = await db.TelCalls
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.CallId, ct);

        if (call is null)
            return Result.Fail(TelephonyErrors.CallNotFound);

        if (call.CrmUserId is { } owner && owner != currentUser.Id)
        {
            logger.LogWarning(
                "User {UserId} of tenant {TenantId} tried to wrap up call {CallId}, which is "
                + "attributed to another agent.",
                currentUser.Id, currentUser.TenantId, call.Id);

            return Result.Fail(TelephonyErrors.CallNotFound);
        }

        var code = cmd.OutcomeCode.Trim().ToUpperInvariant();

        var outcome = await db.TelCallOutcomes
            .Where(o => o.Code == code && o.IsActive)
            .Select(o => new { o.Code, o.CountsAsContact, o.RequiresCallbackDate, o.Label })
            .FirstOrDefaultAsync(ct);

        if (outcome is null)
            return Result.Fail(TelephonyErrors.OutcomeUnknown);

        if (outcome.RequiresCallbackDate && cmd.CallbackAt is null)
            return Result.Fail(TelephonyErrors.CallbackDateRequired);

        // Encrypted before it reaches the aggregate, which takes bytes: the domain must not be
        // able to hold a readable note even briefly.
        var noteEnc = string.IsNullOrWhiteSpace(cmd.Note)
            ? null
            : System.Text.Encoding.UTF8.GetBytes(
                encryptor.Encrypt(cmd.Note.Trim())
                ?? throw new InvalidOperationException(
                    "The integration field encryptor returned nothing for a non-blank note."));

        // The aggregate refuses a call that has not ended — the agent is still speaking — and
        // owns that rule.
        var wrapped = call.WrapUp(outcome.Code, noteEnc, clock.GetUtcNow());

        if (wrapped.IsFailure)
            return wrapped;

        // Identifiers and structured labels only. The note is NOT on the event: it is free text an
        // agent typed about a person, and M13's own timeline projection excludes free-text bodies
        // for exactly that reason — a note crossing into the client timeline would land in a store
        // with no reveal audit. The outcome LABEL travels because it is operator-chosen text, and
        // it saves M13 a lookup into a referential it does not own.
        await publisher.PublishAsync(
            new TelephonyCallWrappedUpEvent(
                TenantId: currentUser.TenantId,
                CallId: call.Id,
                ConnectionId: call.ConnectionId,
                ExternalCallId: call.ExternalCallId,
                EntityType: call.EntityType,
                EntityId: call.EntityId,
                CrmUserId: call.CrmUserId,
                Direction: call.Direction.ToString(),
                Status: call.Status.ToString(),
                OutcomeCode: outcome.Code,
                OutcomeLabel: outcome.Label,
                CountsAsContact: outcome.CountsAsContact,
                StartedAt: call.StartedAt,
                TalkSeconds: call.TalkSeconds,
                CallbackAt: cmd.CallbackAt,
                WrappedUpAt: clock.GetUtcNow()),
            ct);

        await db.SaveChangesAsync(ct);

        // No note, no number. This line is read while somebody investigates a qualification.
        logger.LogInformation(
            "Call {CallId} of tenant {TenantId} wrapped up as {OutcomeCode} by user {UserId}.",
            call.Id, currentUser.TenantId, outcome.Code, currentUser.Id);

        return Result.Ok();
    }
}

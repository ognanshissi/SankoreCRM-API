namespace Sankore.Modules.Integration.Features.Telephony.ClickToCall;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MediatR;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Places the call, synchronously, and records the request that it answers (TEL-B-06).
///
/// <para>
/// <b>Synchronous, with no queue and no automatic retry — and that is the one decision this
/// slice turns on.</b> Every other write that leaves this platform is a queued
/// <c>IntegrationCommand</c> the dispatcher delivers out of band, because a client validated at
/// 23:00 while the core banking system is down must still be created when it comes back. A call
/// is the opposite: the agent is watching the screen waiting for their own telephone to ring, a
/// call delivered four minutes late is a call to somebody who has hung up, and a RETRIED call is
/// a telephone ringing twice in a stranger's hand. There is no idempotency key on
/// <c>ITelephonyPort.StartCallbackAsync</c> for exactly this reason, and no key would make a
/// retry safe.
/// </para>
///
/// <para>
/// <b>The request row is written OUTSIDE the ambient transaction, and it has to be.</b>
/// <c>TransactionBehavior</c> wraps every <c>ICommand</c> in a <c>System.Transactions</c> scope
/// it completes only on a successful <c>Result</c> — so without suppression two things would be
/// wrong at once. The row would not be visible until the handler returned, and the provider rings
/// the agent's handset within a second or two: the resulting call would reach the webhook before
/// the request it answers existed, and the agent's own call would land in the journal as an
/// unattached inbound from a number nobody recognises. And a provider REFUSAL returns
/// <c>Result.Fail</c>, which rolls the scope back — discarding the <c>Failed</c> row that is the
/// only record of an attempt, which is precisely what an operator looks for when an agent says
/// the button does nothing. The same reasoning, and the same remedy, as
/// <c>RevealSensitiveFieldHandler</c>'s quota alert.
/// </para>
///
/// <para>
/// What that costs: a row stuck at <c>Requested</c> if the process dies between the save and the
/// provider's answer. The two-minute expiry closes it, and a request nobody could place is a
/// truer record than no request at all.
/// </para>
/// </summary>
internal sealed class RequestClickToCallHandler(
    IntegrationDbContext db,
    TelephonyAccess access,
    DialableNumberResolver numbers,
    TelephonyNumberProtector protector,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<RequestClickToCallHandler> logger)
    : IRequestHandler<RequestClickToCallCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(RequestClickToCallCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        var tenantId = currentUser.TenantId;
        var actor = currentUser.Id;

        var use = await access.ResolvePortAsync(
            tenantId, IntegrationCapability.ClickToCall, ct);

        if (use.IsFailure)
            return Result.Fail<Guid>(use.Code!);

        var connectionId = use.Value.Connection.Id;

        // ── Who is going to be rung ──────────────────────────────────────────
        // ACTIVE only, unlike the journal's attribution which also accepts a Broken link. The two
        // differ because they answer different questions: "whose conversation was this" is still
        // answerable for an agent whose provider account vanished, while "which handset do I
        // ring" is not — the account is gone, and the provider would refuse or, worse, ring
        // whoever holds that extension now.
        var link = await db.TelUserLinks
            .Where(l => l.ConnectionId == connectionId
                     && l.CrmUserId == actor
                     && l.Status == TelUserLinkStatus.Active)
            .Select(l => new { l.ExternalUserId, l.DefaultCallerNumber })
            .FirstOrDefaultAsync(ct);

        if (link is null)
            return Result.Fail<Guid>(TelephonyErrors.UserNotLinked);

        // ── Which number ─────────────────────────────────────────────────────
        // The owning module applies its own perimeter here. A refusal is relayed as-is: an agent
        // told CLIENT_NOT_FOUND about a record they can see on screen is being told, correctly,
        // that this particular act is not theirs to perform.
        var number = await numbers.ResolveAsync(
            tenantId, cmd.EntityType, cmd.EntityId, cmd.Slot, actor, ct);

        if (number.IsFailure)
            return Result.Fail<Guid>(number.Error!);

        var protectedNumber = protector.Protect(number.Value);

        var request = TelClickToCall.Request(
            tenantId,
            connectionId,
            actor,
            cmd.EntityType,
            cmd.EntityId,
            cmd.Slot.ToString(),
            protectedNumber.Encrypted,
            protectedNumber.BlindIndex,
            clock.GetUtcNow());

        db.TelClickToCalls.Add(request);

        await SaveOutsideAmbientAsync(ct);

        // ── The call ─────────────────────────────────────────────────────────
        // The caller number is the agent's own override, else the connection's default, else
        // nothing — and "nothing" is a valid answer the provider resolves itself. Deciding it here
        // rather than in the adapter keeps the per-agent override visible where an administrator
        // set it.
        var callerNumber = link.DefaultCallerNumber ?? DefaultCallerNumberOf(use.Value.Connection);

        var placed = await use.Value.Port.StartCallbackAsync(
            new ExternalId(link.ExternalUserId), number.Value, callerNumber, ct);

        if (placed.IsFailure)
        {
            request.MarkFailed(placed.Code!);

            // Outside the ambient scope again, and this is the one that would otherwise be lost:
            // the Result.Fail below rolls that scope back.
            await SaveOutsideAmbientAsync(ct);

            // The provider's own detail is logged and NOT returned: it is written for an operator
            // and may name a number. The agent gets one code and a sentence their screen owns.
            logger.LogWarning(
                "Click-to-call {RequestId} for user {UserId} of tenant {TenantId} was refused by "
                + "the provider ({Family}/{Code}).",
                request.Id, actor, tenantId, placed.Family, placed.Code);

            return Result.Fail<Guid>(TelephonyErrors.ProviderRefused);
        }

        request.MarkAgentRinging(placed.Value.Value);

        await SaveOutsideAmbientAsync(ct);

        // No number, no name. This line is read in production while somebody debugs a dialling
        // problem, and §9 forbids a number reaching a log at all.
        logger.LogInformation(
            "Click-to-call {RequestId}: provider {Reference} is ringing user {UserId} of tenant "
            + "{TenantId} for {EntityType} {EntityId}.",
            request.Id, placed.Value.Value, actor, tenantId, cmd.EntityType, cmd.EntityId);

        return Result.Ok(request.Id);
    }

    /// <summary>
    /// Commits this context's pending changes in a transaction of their own, outside the one
    /// <c>TransactionBehavior</c> holds.
    ///
    /// <para>
    /// <c>Complete()</c> is called unconditionally because the scope contains exactly one
    /// <c>SaveChanges</c>: there is no second write for it to be atomic with, and the only
    /// outcomes are "the row is there" and "the save threw", which propagates.
    /// </para>
    /// </summary>
    private async Task SaveOutsideAmbientAsync(CancellationToken ct)
    {
        using var suppressed = new System.Transactions.TransactionScope(
            System.Transactions.TransactionScopeOption.Suppress,
            System.Transactions.TransactionScopeAsyncFlowOption.Enabled);

        await db.SaveChangesAsync(ct);

        suppressed.Complete();
    }

    /// <summary>
    /// The connection's own caller number, when its settings carry one.
    ///
    /// <para>
    /// Pattern-matched on the settings TYPE rather than read through a shared property, because
    /// there is no shared property: <c>ConnectionSettings</c> is a closed hierarchy and only a
    /// telephony kind has a caller number at all. A kind added later without one simply answers
    /// null, which is the same as an unconfigured default — not a failure.
    /// </para>
    /// </summary>
    private static string? DefaultCallerNumberOf(IntegrationConnection connection)
        => connection.Settings switch
        {
            RingoverSettings ringover => ringover.DefaultCallerNumber,
            _ => null,
        };
}

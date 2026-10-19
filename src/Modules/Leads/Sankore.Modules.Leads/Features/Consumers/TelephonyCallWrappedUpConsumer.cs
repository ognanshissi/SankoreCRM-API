namespace Sankore.Modules.Leads.Features.Consumers;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Turns a qualified telephony call into the lead's own history (TEL-B-11) and stops its
/// first-contact clock when the outcome says a human was reached (TEL-B-12).
///
/// <para>
/// <b>Not behind MediatR, unlike every other write in this module.</b> The same reason M01's
/// <c>HoldingSyncService</c> gives: the caller is a consumer under SYSTEM identity, so
/// <c>AgencyAuthorizationBehavior</c> would check a perimeter SYSTEM does not have, and
/// <c>AuditBehavior</c> would record SYSTEM as the author of a change the agent made. The agent is
/// already named — on the activity's <c>PerformedBy</c> and on the telephony module's own audit
/// row for the wrap-up.
/// </para>
///
/// <para>
/// <b>Idempotent on the CALL, not on the message.</b> Delivery is at-least-once AND an agent may
/// legitimately wrap the same call up twice to correct an outcome, so neither an inbox table nor a
/// message id would do: the anchor is <c>LeadActivity.CtiCallReference</c>, which carries the
/// provider's own call identifier and is unique per tenant
/// (<c>ux_lead_activity_cti_call</c>). The read below is the friendly answer; the index is the
/// guarantee.
/// </para>
///
/// <para>
/// <b>A corrected outcome does NOT rewrite the activity, and that is deliberate.</b> An activity
/// is documented as immutable and never mutated after creation; re-qualifying a call is a second
/// opinion about a conversation, not a different conversation. The telephony journal holds the
/// current outcome — it is the authority on that — and the lead's history holds what was first
/// recorded, which is what a reader of a history expects.
/// </para>
/// </summary>
public sealed class TelephonyCallWrappedUpConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<TelephonyCallWrappedUpConsumer> logger)
    : IConsumer<TelephonyCallWrappedUpEvent>
{
    public async Task Consume(ConsumeContext<TelephonyCallWrappedUpEvent> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var evt = context.Message;

        if (evt.TenantId == Guid.Empty) return;

        // Cheap pre-check so a client's call does not cost a DI scope — the authoritative one is
        // in ApplyAsync, which is where a test can reach it.
        if (evt.EntityType != TelephonyEntityTypes.Lead || evt.EntityId is null) return;

        // The ambient SYSTEM scope must be established BEFORE any scoped service is resolved:
        // ITenantContext and ICurrentUser are built from it, and a consumer has no HTTP context.
        using var bgCtx = BackgroundJobContext.SetScope(evt.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<LeadsDbContext>();

        await ApplyAsync(db, logger, evt, CancellationToken.None);
    }

    /// <summary>
    /// The whole body, with its context passed in so a test can pin it without MassTransit or a
    /// DI container.
    ///
    /// <para>
    /// <b>One transaction for the three writes.</b> The activity, the SLA stop and the callback
    /// reminder commit together: an activity without its SLA stop would leave a lead showing an
    /// overdue first contact for a call that is in its own history, and a reminder without its
    /// activity would chase an agent about a conversation the lead does not record.
    /// </para>
    /// </summary>
    internal static async Task ApplyAsync(
        LeadsDbContext db,
        ILogger logger,
        TelephonyCallWrappedUpEvent evt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(evt);

        // THE guard, and it lives here rather than only in Consume above: it is a correctness rule
        // — a client's call written as a lead activity would be filed against whatever lead the id
        // happened to match — and a rule that only the MassTransit entry point enforces is a rule
        // no test of this method can pin.
        //
        // Null is ordinary and not an error: most inbound calls are from somebody not on file.
        if (evt.EntityType != TelephonyEntityTypes.Lead || evt.EntityId is not { } leadId)
            return;

        // IgnoreQueryFilters with an explicit tenant predicate: a consumer's ambient tenant is the
        // one it just set, and relying on that rather than saying it is how a replay against
        // another tenant's lead becomes possible.
        var lead = await db.Leads
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(l => l.TenantId == evt.TenantId && l.Id == leadId, ct);

        if (lead is null)
        {
            // An at-least-once consumer must not blow up on a lead that was purged or anonymised
            // between the call and the message.
            logger.LogInformation(
                "Telephony wrap-up of call {CallId}: lead {LeadId} of tenant {TenantId} no longer "
                + "exists; nothing recorded.", evt.CallId, leadId, evt.TenantId);

            return;
        }

        var alreadyLogged = await db.LeadActivities
            .IgnoreQueryFilters()
            .AnyAsync(
                a => a.TenantId == evt.TenantId && a.CtiCallReference == evt.ExternalCallId, ct);

        if (alreadyLogged)
        {
            logger.LogDebug(
                "Telephony wrap-up of call {CallId} is already in lead {LeadId}'s history; "
                + "skipped.", evt.CallId, leadId);

            return;
        }

        // PerformedBy is the AGENT and not SYSTEM: they had the conversation. A system-generated
        // flag says the row was written by machinery, which is a different fact — and the two
        // being separate is what lets an agent performance figure count this call.
        //
        // Guid.Empty when the call was never attributed to a CRM user, which is ordinary: an
        // unlinked provider account's calls are journalled and listed under "calls to attribute".
        var performedBy = evt.CrmUserId ?? Guid.Empty;

        db.LeadActivities.Add(LeadActivity.Create(
            tenantId: evt.TenantId,
            leadId: leadId,
            type: ActivityType.Call,
            // The outcome's own LABEL, which is operator-chosen text from the tenant's
            // referential. NEVER a number: this subject is projected into the client timeline at
            // conversion, where TimelineSummaryGuard redacts anything that looks like one and logs
            // a warning about it — so a subject built from a number would be both redacted and an
            // alert, every single call.
            subject: Subject(evt),
            performedBy: performedBy,
            // No notes. The agent's note stays in the telephony module, encrypted: M13's own
            // timeline projection excludes free-text bodies because they cross into a store with
            // no reveal audit, and the event deliberately does not carry it.
            notes: null,
            durationMinutes: ToMinutes(evt.TalkSeconds),
            outcome: ToOutcome(evt),
            ctiCallReference: evt.ExternalCallId,
            isSystemGenerated: true,
            // The instant the CALL happened, not the instant it was qualified. Until TEL-B-11 this
            // was forced to UtcNow, so an activity was dated by the length of the agent's
            // paperwork — see LeadActivity.PerformedAt.
            performedAt: evt.StartedAt));

        lead.RecordActivity();

        var stoppedSla = false;

        // ── TEL-B-12 ─────────────────────────────────────────────────────────
        // Only an outcome the TENANT marked as counting. Leaving a message is not reaching
        // somebody, and the institution decides which of its outcomes mean what.
        if (evt.CountsAsContact && lead.CurrentAssignmentId is { } assignmentId)
        {
            var assignment = await db.LeadAssignments
                .IgnoreQueryFilters()
                .AsTracking()
                .FirstOrDefaultAsync(
                    a => a.TenantId == evt.TenantId && a.Id == assignmentId, ct);

            // Dated at the CALL, not at the wrap-up: the SLA question is "how long before somebody
            // spoke to this lead", and answering it with the moment the agent finished typing
            // flatters every figure by the length of their paperwork.
            stoppedSla = assignment?.RecordFirstContact(
                evt.StartedAt, FirstContactSource.TelephonyCall) == true;

            // A qualified call is a first contact too: it closes the assignment's task, exactly as
            // the agent's own declaration does.
            if (stoppedSla)
                await Features.Tasks.AssignmentTasks.CompleteFirstContactAsync(
                    db, evt.TenantId, leadId, assignment!.AgentId, ct);
        }

        // ── What the call says about the lead ────────────────────────────────
        // An outcome the tenant counts as a contact makes the lead "Contacté", assignment or not:
        // somebody spoke to them. An outbound call that did not count is an attempt.
        if (evt.CountsAsContact)
            lead.RecordContactEstablished();
        else if (string.Equals(evt.Direction, "Outbound", StringComparison.OrdinalIgnoreCase))
            lead.RecordContactAttempt();

        // ── The callback the outcome demanded ────────────────────────────────
        if (evt.CallbackAt is { } callbackAt)
        {
            db.LeadReminders.Add(LeadReminder.Create(
                tenantId: evt.TenantId,
                leadId: leadId,
                // The outcome's label again, not a sentence built here: an institution that renamed
                // "À rappeler" sees its own words on the reminder.
                title: $"Rappel — {evt.OutcomeLabel}",
                createdBy: performedBy,
                dueAt: callbackAt));
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Telephony wrap-up of call {CallId} recorded on lead {LeadId} of tenant {TenantId} "
            + "as {OutcomeCode} (SLA stopped: {StoppedSla}, callback: {HasCallback}).",
            evt.CallId, leadId, evt.TenantId, evt.OutcomeCode, stoppedSla,
            evt.CallbackAt is not null);
    }

    /// <summary>
    /// What the lead's history says this activity was.
    ///
    /// <para>
    /// Direction first because it is the question a reader of a history asks — "did we call them
    /// or did they call us" — and it is the one thing the outcome label never says.
    /// </para>
    /// </summary>
    private static string Subject(TelephonyCallWrappedUpEvent evt)
    {
        var direction = string.Equals(evt.Direction, "Outbound", StringComparison.OrdinalIgnoreCase)
            ? "Appel sortant"
            : "Appel entrant";

        return $"{direction} — {evt.OutcomeLabel}";
    }

    /// <summary>
    /// Talk seconds as whole minutes, rounded UP, and zero stays null.
    ///
    /// <para>
    /// Rounded up because <c>DurationMinutes</c> feeds "time spent" figures and a floor would
    /// report a day of forty-second calls as zero minutes of work. Zero seconds — a call that was
    /// never answered — answers null rather than 0: "no conversation" and "a conversation of under
    /// a minute" are different, and a column that cannot tell them apart makes an average
    /// meaningless.
    /// </para>
    /// </summary>
    private static int? ToMinutes(int? talkSeconds)
        => talkSeconds is > 0 ? (talkSeconds.Value + 59) / 60 : null;

    /// <summary>
    /// The tenant's outcome code folded onto M13's own <see cref="ActivityOutcome"/>.
    ///
    /// <para>
    /// <b>A mapping and not a passthrough, because the two lists answer different questions.</b>
    /// <c>ActivityOutcome</c> is a platform enum shared by calls, meetings and visits;
    /// <c>tel_call_outcome</c> is a per-tenant referential an institution extends. So an outcome
    /// this mapping does not recognise — and an institution WILL add one — degrades to the
    /// <c>CountsAsContact</c> flag the event already carries, which is the only part of its
    /// meaning the platform is entitled to assume. Null would have been the other option and is
    /// worse: it reads as "nobody recorded an outcome" on a call that was qualified.
    /// </para>
    /// </summary>
    private static ActivityOutcome ToOutcome(TelephonyCallWrappedUpEvent evt)
        => evt.OutcomeCode switch
        {
            "REACHED" => ActivityOutcome.Reached,
            "INTERESTED" => ActivityOutcome.Interested,
            "NOT_INTERESTED" => ActivityOutcome.NotInterested,
            "CALLBACK" => ActivityOutcome.Callback,
            "VOICEMAIL" => ActivityOutcome.Voicemail,
            "NOT_REACHED" or "WRONG_NUMBER" => ActivityOutcome.NoAnswer,
            _ => evt.CountsAsContact ? ActivityOutcome.Reached : ActivityOutcome.NoAnswer,
        };
}

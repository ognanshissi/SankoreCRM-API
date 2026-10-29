namespace Sankore.Modules.Leads;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// The only implementation of <see cref="ILeadsModule"/>: the seam through which
/// other modules read lead data without referencing this assembly.
///
/// Every method takes (or derives) an explicit tenant id and queries with
/// <c>IgnoreQueryFilters()</c> plus a manual <c>TenantId ==</c> predicate, because
/// callers may be MassTransit consumers or Hangfire jobs where the ambient
/// <see cref="Sankore.Shared.Kernel.ITenantContext"/> is not the tenant being read.
/// </summary>
public sealed class LeadsModuleFacade(
    LeadsDbContext db,
    IPhoneBlindIndexer phoneBlindIndexer,
    ICurrentUser currentUser,
    Features.Household.LeadAccessGuard accessGuard,
    MediatR.ISender? sender = null,
    Features.Questionnaires.IProjectAnswersSnapshots? projectAnswers = null,
    TimeProvider? clock = null,
    Features.FindDuplicates.ILeadEmailIndexer? emailIndexer = null)
    : ILeadsModule
{
    public async Task<ProjectAnswersSnapshot?> GetProjectAnswersSnapshotAsync(
        Guid tenantId, Guid opportunityId, CancellationToken ct)
        => projectAnswers is null ? null : await projectAnswers.BuildAsync(tenantId, opportunityId, ct);

    public Task MarkProjectAnswersTransmittedAsync(Guid tenantId, Guid opportunityId, CancellationToken ct)
        => projectAnswers is null
            ? Task.CompletedTask
            : projectAnswers.MarkTransmittedAsync(
                tenantId, opportunityId, (clock ?? TimeProvider.System).GetUtcNow(), ct);

    /// <summary>
    /// The code this module has used for a missing lead since its first handler. A literal in
    /// seven places and no <c>LeadErrors</c> class to put it in — named here rather than typed an
    /// eighth time, because a facade answering <c>LEAD_NOT_FOUND</c> with a typo is a front end
    /// that shows a generic failure.
    /// </summary>
    private const string LeadNotFound = "LEAD_NOT_FOUND";

    /// <summary>
    /// The slot holds no number. Distinct from <see cref="LeadNotFound"/> on purpose: the first
    /// means the agent may not dial this lead, the second that there is nothing to dial, and the
    /// screen says two different things.
    /// </summary>
    private const string PhoneSlotEmpty = "LEAD_PHONE_SLOT_EMPTY";

    /// <summary>A missed-call capture with no agent to own the lead (TEL-B-13).</summary>
    private const string MissedCallOwnerRequired = "LEAD_MISSED_CALL_OWNER_REQUIRED";

    /// <summary>
    /// The insert failed and the re-read found nothing either. Its own code rather than a rethrow:
    /// the caller is journalling a call and must be able to carry on without a lead.
    /// </summary>
    private const string MissedCallCaptureFailed = "LEAD_MISSED_CALL_CAPTURE_FAILED";

    public async Task<LeadErasureSubject?> GetErasureSubjectAsync(Guid tenantId, Guid leadId, CancellationToken ct)
        => await db.Leads
            .IgnoreQueryFilters()
            .Where(l => l.TenantId == tenantId && l.Id == leadId)
            .Select(l => new LeadErasureSubject(
                l.Id,
                // The first eight characters of the id: what an audit row can quote without the name.
                "Lead " + l.Id.ToString().Substring(0, 8),
                l.IsAnonymized,
                l.ConvertedToCustomerId ?? l.CustomerId))
            .FirstOrDefaultAsync(ct);

    public Task<bool> EraseLeadAsync(Guid tenantId, Guid leadId, CancellationToken ct)
        => (sender ?? throw new InvalidOperationException("Erasing a lead needs the mediator."))
            .Send(new Features.Retention.EraseLeadCommand(tenantId, leadId), ct);

    public async Task<IReadOnlyDictionary<Guid, string>> GetLeadNamesAsync(IReadOnlyCollection<Guid> leadIds, CancellationToken ct)
    {
        if (leadIds.Count == 0) return new Dictionary<Guid, string>();
        var ids = leadIds.Distinct().ToList();
        return await db.Leads.Where(l => ids.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.FullName, ct);
    }

    public async Task<LeadNotificationFacts?> GetNotificationFactsAsync(
        Guid tenantId, string workflowEntityType, Guid entityId, CancellationToken ct)
    {
        Guid leadId;
        Guid? projectOwner = null;
        string? projectProduct = null;
        string link;

        if (string.Equals(workflowEntityType, "Opportunity", StringComparison.OrdinalIgnoreCase))
        {
            var project = await db.Opportunities
                .IgnoreQueryFilters()
                .Where(o => o.TenantId == tenantId && o.Id == entityId)
                .Select(o => new { o.LeadId, o.OwnerId, o.Product })
                .FirstOrDefaultAsync(ct);
            if (project is null) return null;

            link = $"/leads/projets/{entityId}";
            projectOwner = project.OwnerId;
            projectProduct = project.Product;
            if (project.LeadId is not { } owningLead)
                return new LeadNotificationFacts(null, projectOwner, null, null, projectProduct, link);
            leadId = owningLead;
        }
        else
        {
            leadId = entityId;
            link = $"/leads/{entityId}";
        }

        var lead = await db.Leads
            .IgnoreQueryFilters()
            .Where(l => l.TenantId == tenantId && l.Id == leadId)
            .Select(l => new { l.AgencyId, l.CurrentAssignedId, l.OwnerId, l.FirstName, l.LastName, l.FullName, l.InterestedProduct })
            .FirstOrDefaultAsync(ct);
        if (lead is null)
            return projectOwner is null && projectProduct is null
                ? null
                : new LeadNotificationFacts(null, projectOwner, null, null, projectProduct, link);

        return new LeadNotificationFacts(
            lead.AgencyId,
            projectOwner ?? lead.CurrentAssignedId ?? lead.OwnerId,
            lead.FirstName,
            lead.LastName ?? (lead.FirstName is null ? lead.FullName : null),
            projectProduct ?? Features.WorkflowNotifications.LeadNotificationValues.Product(lead.InterestedProduct),
            link);
    }

    public async Task<LeadSummary?> GetLeadAsync(Guid leadId, CancellationToken ct)
    {
        var lead = await db.Leads
            .Where(l => l.Id == leadId)
            .Select(l => new LeadSummary(
                l.Id,
                l.FullName,
                l.Status.ToString(),
                l.Source.ToString(),
                l.CurrentAssignedId))
            .FirstOrDefaultAsync(ct);

        return lead;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Privacy contract — read before adding a field to any summary below:
    /// only STRUCTURED, operator-chosen labels are projected (activity type, subject,
    /// outcome, reminder title, status). Free-text bodies (<c>LeadActivity.Notes</c>,
    /// <c>LeadReminder.Notes</c>) are deliberately excluded: an agent may paste a phone
    /// number or a document number into them, and this projection crosses a module
    /// boundary into a store (the client timeline) that has no reveal audit. Likewise
    /// excluded: <c>VisitLocation</c> (GPS), <c>VisitPhotoReference</c>,
    /// <c>CtiCallReference</c> and every field of the lead itself
    /// (phone, e-mail, national id, date of birth).
    /// </remarks>
    public Task<IReadOnlyList<CustomerDocumentEntry>> ListCustomerDocumentsAsync(
        Guid tenantId, Guid customerId, CancellationToken ct)
        => Features.CustomerDocuments.LeadCustomerDocumentReader.ReadAsync(db, tenantId, customerId, ct);

    public async Task<IReadOnlyList<CustomerOpenLead>> GetOpenLeadsForCustomerAsync(
        Guid tenantId, Guid customerId, CancellationToken ct)
    {
        if (customerId == Guid.Empty)
            return [];

        // IgnoreQueryFilters plus an explicit tenant predicate: the caller is M01's client screen
        // or a consumer, and its ambient tenant is not guaranteed to be this one.
        //
        // Status filter rather than Lead.IsDispatchable: "open" here means "still being worked",
        // which is the same set of terminal statuses but a different question — a Nurturing lead is
        // not dispatchable and IS an open request the client record must show.
        return await db.Leads
            .IgnoreQueryFilters()
            .Where(l => l.TenantId == tenantId
                     && l.CustomerId == customerId
                     && l.Status != LeadStatus.Converted
                     && l.Status != LeadStatus.Lost
                     && l.Status != LeadStatus.Disqualified
                     && l.Status != LeadStatus.Archived)
            .OrderByDescending(l => l.CapturedAt)
            .Select(l => new CustomerOpenLead(
                l.Id,
                l.Status.ToString(),
                l.PipelineStage.ToString(),
                // The lead stores no product line: its open PROJECT carries the one being sold.
                db.Opportunities
                    .IgnoreQueryFilters()
                    .Where(o => o.TenantId == tenantId && o.LeadId == l.Id
                             && o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost)
                    .OrderByDescending(o => o.CreatedAt)
                    .Select(o => o.ProductLineId)
                    .FirstOrDefault(),
                l.InterestedProduct,
                l.CurrentAssignedId,
                l.CapturedAt,
                l.LastActivityAt))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Two predicates for one question, and the second one is transitional. The index is the
    /// indexed, exact half; <c>PhoneBlindIndex == null</c> marks a lead captured before the
    /// protection migration, matched on the suffix of the clear column the way
    /// <c>CaptureLeadHandler</c>'s duplicate gate does. Dropping the fallback would make the
    /// caller-id lookup miss exactly the oldest leads, which is where the longest-running
    /// conversations are.
    /// </para>
    /// <para>
    /// The index is computed HERE, from the clear number, and that is the only arrangement that
    /// can work: <c>Leads:BlindIndexKey</c> is this module's, so a caller computing it would
    /// compute it under its own key and match nothing — silently, since an index comparison
    /// that finds no row is indistinguishable from a number nobody called from.
    /// </para>
    /// <para>
    /// <c>IsAnonymized</c> is excluded explicitly although the anonymisation already clears the
    /// index and writes a marker into the clear column, so neither predicate could match one: a
    /// retention rule is not something to leave resting on two implementation details in another
    /// file.
    /// </para>
    /// </remarks>
    /// <inheritdoc />
    /// <remarks>
    /// Never <c>l.Email == x</c>: the column is non-deterministic ciphertext (see CLAUDE.md « Lead
    /// e-mail is encrypted »). The blind index, plus the transitional clear column for rows the
    /// backfill has not reached.
    /// </remarks>
    public async Task<IReadOnlyList<LeadEmailMatch>> MatchLeadsByEmailsAsync(
        Guid tenantId, IReadOnlyCollection<string> emails, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || emailIndexer is null || emails.Count == 0) return [];

        var byIndex = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var email in emails.Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Length > 0).Distinct())
        {
            if (emailIndexer.Compute(email) is { } index) byIndex[index] = email;
        }

        if (byIndex.Count == 0) return [];

        var indexes = byIndex.Keys.ToList();
        var clears = byIndex.Values.ToList();

        var rows = await db.Leads
            .IgnoreQueryFilters()
            .Where(l => l.TenantId == tenantId && !l.IsAnonymized
                     && ((l.EmailBlindIndex != null && indexes.Contains(l.EmailBlindIndex))
                      || (l.EmailBlindIndex == null && l.LegacyEmailClear != null && clears.Contains(l.LegacyEmailClear.ToLower()))))
            .Select(l => new { l.Id, l.EmailBlindIndex, l.LegacyEmailClear })
            .ToListAsync(ct);

        return rows
            .Select(r => new LeadEmailMatch(
                r.EmailBlindIndex is { } i ? byIndex[i] : r.LegacyEmailClear!.Trim().ToLowerInvariant(), r.Id))
            .ToList();
    }

    public async Task<IReadOnlyList<LeadPhoneMatch>> FindOpenLeadsByPhoneAsync(
        Guid tenantId, string phoneNumber, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || string.IsNullOrWhiteSpace(phoneNumber))
            return [];

        return await Features.FindDuplicates.OpenLeadsByPhone.FindAsync(
            db, phoneBlindIndexer, tenantId, phoneNumber, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>No MediatR, like the other members of this facade.</b> The caller is the telephony
    /// module inside an HTTP request of its own; dispatching <c>CaptureLeadCommand</c> would run a
    /// second validation pipeline and a second audit row for one act, and would drag in the
    /// duplicate-gate machinery this method deliberately replaces with a narrower rule.
    /// </para>
    /// <para>
    /// <b>The existing-lead check is inside this method and not only in the caller.</b> The
    /// telephony side asks the matcher first — it has to, because a number may belong to a CLIENT,
    /// which is not this module's business — but two calls arriving in the same second would both
    /// pass that read. This one is the guarantee, and it is cheap: the same indexed blind-index
    /// lookup <c>FindOpenLeadsByPhoneAsync</c> already does.
    /// </para>
    /// </remarks>
    public async Task<Result<MissedCallLeadResult>> CaptureFromMissedCallAsync(
        Guid tenantId,
        string phoneNumber,
        Guid ownerId,
        Guid? agencyId,
        DateTimeOffset missedAt,
        CancellationToken ct)
    {
        if (tenantId == Guid.Empty || string.IsNullOrWhiteSpace(phoneNumber))
            return Result.Fail<MissedCallLeadResult>(LeadNotFound);

        if (ownerId == Guid.Empty)
            return Result.Fail<MissedCallLeadResult>(MissedCallOwnerRequired);

        var existing = await FindOpenLeadsByPhoneAsync(tenantId, phoneNumber, ct);

        // Several open leads already share the number — a family handset. Answering the newest is
        // the best this method can do and is better than creating a third: the agent sees the
        // call attached to somebody they can correct, rather than to a record nobody has ever
        // spoken to.
        if (existing.Count > 0)
            return Result.Ok(new MissedCallLeadResult(existing[0].LeadId, Created: false));

        var trimmed = phoneNumber.Trim();

        var lead = Lead.Capture(
            tenantId: tenantId,
            // The placeholder. Dated so two of them in one agent's list are distinguishable, and
            // worded so nobody mistakes it for a name somebody gave us.
            fullName: $"Appel manqué du {missedAt:dd/MM/yyyy HH:mm}",
            phoneNumber: trimmed,
            source: LeadSource.CallCenter,
            // "Unknown" is the literal this module already uses for "no product was named", and
            // ResolveProductLineAsync is documented to resolve it to nothing — so an unanswered
            // call inherits no product line rather than the policy of a product nobody asked for.
            interestedProduct: "Unknown",
            preferredLanguage: LanguageCode.Default,
            // A missed call carries no location. GeoPoint is required by the factory, and the
            // tenant's own capture screens pass the agency's — there is none here, so the origin
            // is passed and the lead simply has no geographic signal for the dispatcher.
            location: new GeoPoint(0, 0),
            preferredAgencyId: agencyId,
            clock: TimeProvider.System,
            // The agent the call was for. A lead created from a missed call and routed to nobody
            // would sit in a queue with no SLA and no owner.
            ownerId: ownerId,
            agencyId: agencyId,
            phoneBlindIndex: phoneBlindIndexer.Compute(trimmed));

        db.Leads.Add(lead);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race to a concurrent call from the same number. Re-read and answer the
            // winner: the caller wants a lead to attach its call to, not to have been the one who
            // created it.
            var winner = await FindOpenLeadsByPhoneAsync(tenantId, trimmed, ct);

            return winner.Count > 0
                ? Result.Ok(new MissedCallLeadResult(winner[0].LeadId, Created: false))
                : Result.Fail<MissedCallLeadResult>(MissedCallCaptureFailed);
        }

        return Result.Ok(new MissedCallLeadResult(lead.Id, Created: true));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Delegates to <c>LeadAccessGuard</c>, which is the single definition — see
    /// <see cref="ResolvePhoneForCallAsync"/>'s remarks for the actor rule, which is the same here
    /// and for the same reason.
    /// </remarks>
    public async Task<bool> CanActorWorkLeadAsync(
        Guid tenantId, Guid leadId, Guid actorUserId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || leadId == Guid.Empty || actorUserId == Guid.Empty)
            return false;

        if (actorUserId != currentUser.Id || tenantId != currentUser.TenantId)
            return false;

        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct);

        if (lead is null || lead.IsAnonymized)
            return false;

        return (await accessGuard.EnsureCanWorkAsync(lead, ct)).IsSuccess;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Why the actor is checked against the ambient identity instead of being trusted.</b>
    /// The ownership rule lives in <c>LeadAccessGuard</c>, which reads <c>ICurrentUser</c> — it
    /// has to, because "is the caller a supervisor of whoever holds this lead" is answered from
    /// the roles on the token. Taking the actor as a parameter and then evaluating the rule
    /// against somebody else would be this module vouching for a check it never made, so the two
    /// are required to agree and a mismatch is refused. The practical effect is that only an
    /// authenticated request can dial, which is the only caller TEL-B-06 has.
    /// </para>
    /// <para>
    /// No <c>IgnoreQueryFilters</c> here, unlike every other method on this facade: the caller is
    /// inside the agent's own HTTP request, so the ambient tenant IS this tenant — and relying on
    /// the global filter means a tenant mismatch yields nothing rather than something.
    /// </para>
    /// </remarks>
    public async Task<Result<string>> ResolvePhoneForCallAsync(
        Guid tenantId, Guid leadId, PhoneSlot slot, Guid actorUserId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || leadId == Guid.Empty)
            return Result.Fail<string>(LeadNotFound);

        if (actorUserId == Guid.Empty || actorUserId != currentUser.Id)
            return Result.Fail<string>(Features.Household.LeadAccessGuard.Forbidden);

        if (tenantId != currentUser.TenantId)
            return Result.Fail<string>(LeadNotFound);

        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct);

        if (lead is null)
            return Result.Fail<string>(LeadNotFound);

        // An anonymised lead's number is the marker string, not a number — and ringing it would
        // be acting on a record whose retention window has closed.
        if (lead.IsAnonymized)
            return Result.Fail<string>(LeadNotFound);

        var access = await accessGuard.EnsureCanWorkAsync(lead, ct);

        if (access.IsFailure)
            return Result.Fail<string>(access.Error!);

        // Lead.PhoneNumber is CLEAR in the column — only Lead.Email is encrypted on this
        // aggregate (see the module's note on the asymmetry). So there is nothing to decrypt
        // here, which is also why no key problem can arise on this path.
        var number = slot switch
        {
            PhoneSlot.Primary => lead.PhoneNumber,
            PhoneSlot.Secondary => lead.CompanyPhone,
            _ => null,
        };

        return string.IsNullOrWhiteSpace(number)
            ? Result.Fail<string>(PhoneSlotEmpty)
            : Result.Ok(number.Trim());
    }

    public async Task<IReadOnlyList<LeadHistoryItem>> GetLeadHistoryAsync(
        Guid tenantId, Guid leadId, CancellationToken ct)
    {
        var leadExists = await db.Leads
            .IgnoreQueryFilters()
            .AnyAsync(l => l.TenantId == tenantId && l.Id == leadId, ct);

        // An at-least-once consumer must not blow up on a lead that no longer exists.
        if (!leadExists) return [];

        var items = new List<LeadHistoryItem>();

        // ── Activities (calls, meetings, e-mails, notes…) and field visits ──────
        var activities = await db.LeadActivities
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && a.LeadId == leadId)
            .Select(a => new
            {
                a.Id,
                a.Type,
                a.Subject,
                a.Outcome,
                a.PerformedAt,
                a.DurationMinutes,
                a.IsSystemGenerated,
            })
            .ToListAsync(ct);

        foreach (var a in activities)
        {
            var entryType = a.Type == ActivityType.Visit
                ? "LEAD_VISIT"
                : a.Type == ActivityType.Note
                    ? "LEAD_NOTE"
                    : $"LEAD_ACTIVITY_{a.Type.ToString().ToUpperInvariant()}";

            var summary = $"{Label(a.Type)} : {Trim(a.Subject)}";
            if (a.Outcome is not null)
                summary += $" — issue : {a.Outcome}";
            if (a.DurationMinutes is > 0)
                summary += $" ({a.DurationMinutes} min)";
            if (a.IsSystemGenerated)
                summary += " [automatique]";

            items.Add(new LeadHistoryItem(entryType, a.PerformedAt, summary, "LeadActivity", a.Id.ToString("D")));
        }

        // ── Follow-up reminders ────────────────────────────────────────────────
        var reminders = await db.LeadReminders
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.LeadId == leadId)
            .Select(r => new { r.Id, r.Title, r.Status, r.DueAt, r.CreatedAt, r.ResolvedAt })
            .ToListAsync(ct);

        foreach (var r in reminders)
        {
            var summary = $"Relance « {Trim(r.Title)} » — {r.Status}";
            items.Add(new LeadHistoryItem(
                "LEAD_REMINDER",
                r.ResolvedAt ?? r.CreatedAt,
                summary,
                "LeadReminder",
                r.Id.ToString("D")));
        }

        return items.OrderBy(i => i.OccurredAt).ToList();
    }

    private static string Label(ActivityType type) => type switch
    {
        ActivityType.Call => "Appel",
        ActivityType.Meeting => "Rendez-vous",
        ActivityType.Email => "E-mail envoyé",
        ActivityType.Visit => "Visite terrain",
        ActivityType.Note => "Note",
        ActivityType.Sms => "SMS envoyé",
        ActivityType.WhatsApp => "Message WhatsApp",
        ActivityType.Task => "Tâche",
        _ => type.ToString(),
    };

    /// <summary>Keeps a summary well inside the 500-character timeline column.</summary>
    private static string Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(sans objet)";
        var trimmed = value.Trim();
        return trimmed.Length <= 180 ? trimmed : trimmed[..180] + "…";
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Through MediatR, unlike the rest of this facade</b>, because this is the « thick command »
    /// the integration module's « thin job » hands its work to: attaching a message, opening a
    /// conversation, creating a lead from an unknown number and recording consent are this
    /// module's decisions and belong in a handler, not in a facade method. Optional in the
    /// constructor only for this module's tests, which build the facade by hand and never call it.
    /// </remarks>
    public Task<MessagingEventsOutcome> RecordMessagingEventsAsync(
        Guid tenantId,
        Guid connectionId,
        Guid? connectionAgencyId,
        MessagingEventBatch batch,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (sender is null)
            throw new InvalidOperationException("ISender is not available to the Leads facade.");

        if (tenantId == Guid.Empty || (batch.Messages.Count == 0 && batch.Statuses.Count == 0))
            return Task.FromResult(new MessagingEventsOutcome(0, 0, 0, 0));

        return sender.Send(
            new Features.WhatsApp.RecordMessagingEventsCommand(tenantId, connectionId, connectionAgencyId, batch), ct);
    }

    public async Task<AppointmentCalendarEntry?> GetAppointmentForCalendarAsync(Guid tenantId, Guid appointmentId, CancellationToken ct)
    {
        var appointment = await db.Appointments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Id == appointmentId, ct);

        return appointment is null ? null : ToCalendarEntry(appointment);
    }

    public async Task<IReadOnlyList<AppointmentCalendarEntry>> ListAppointmentsForUserAsync(
        Guid tenantId, Guid userId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        // The end is a computed property: filter the start in SQL with a day of margin (no
        // appointment lasts longer), then exactly in memory.
        var earliestStart = from.AddDays(-1);
        var appointments = await db.Appointments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId
                && a.Status == AppointmentStatus.Scheduled
                && a.StartAt < to && a.StartAt >= earliestStart
                && (a.OrganizerId == userId || a.ParticipantIds.Contains(userId)))
            .OrderBy(a => a.StartAt)
            .Take(500)
            .ToListAsync(ct);

        return appointments.Where(a => a.EndAt > from).Select(ToCalendarEntry).ToList();
    }

    private static AppointmentCalendarEntry ToCalendarEntry(Appointment a) => new(
        a.Id, a.Title, a.StartAt, a.EndAt, a.Status == AppointmentStatus.Cancelled,
        a.Location, a.MeetingUrl, a.OrganizerId, a.ParticipantIds.ToList());
}

using Sankore.Modules.Leads.Domain.Events;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Money = Sankore.Shared.Kernel.ValueObject.Money;

namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Aggregate root for a prospect prior to conversion into a Customer (module M01).
/// Implements the full M13 lead lifecycle: Capture → Qualify → Assign → Convert.
/// Status tracks high-level lifecycle; PipelineStage tracks sales funnel position.
/// </summary>
public sealed class Lead : AggregateRoot
{
    // ── Identity ────────────────────────────────────────────────────────────

    public Guid Id { get; private set; }

    /// <summary>Full name (required). FirstName + LastName stored separately when available.</summary>
    public string FullName { get; private set; } = default!;
    public string? FirstName { get; private set; }
    public string? LastName { get; private set; }
    public string PhoneNumber { get; private set; } = default!;
    /// <summary>HMAC-SHA256 blind index on the normalized phone (last 8 digits). Used for dedup queries.</summary>
    public string? PhoneBlindIndex { get; private set; }
    public string? Email { get; private set; }
    public LeadGender Gender { get; private set; } = LeadGender.Unknown;
    public DateOnly? DateOfBirth { get; private set; }

    /// <summary>Individual (B2C) or Corporate (B2B) prospect.</summary>
    public LeadType ProspectType { get; private set; } = LeadType.Individual;

    // ── Organisation (optional, for corporate leads) ────────────────────────
    public string? CompanyName { get; private set; }
    public string? CompanyEmail { get; private set; }
    public string? CompanyPhone { get; private set; }
    public string? Website { get; private set; }

    // ── Capture context ──────────────────────────────────────────────────────
    public LeadStatus Status { get; private set; }
    public PipelineStage PipelineStage { get; private set; }
    public LeadSource Source { get; private set; }

    /// <summary>
    /// The configured <c>LeadSourceConfig</c> this lead was ingested through, or null when there
    /// is none — a lead typed into the UI, imported from a file, or produced by a merge never
    /// arrived through a configured source, and null says exactly that.
    ///
    /// Distinct from <see cref="Source"/> and not derivable from it: that enum is a coarse
    /// reporting axis (<c>LeadSource</c>), while a source config carries a different one
    /// (<c>LeadChannelType</c>) plus the dispatching rule, cost-per-lead and dedup window that
    /// actually govern this lead. Before this column existed the link lived only on
    /// <c>LeadIngestion.SourceId</c>, so every reader had to join through a side table.
    ///
    /// An OPAQUE reference: no physical foreign key, same as <c>LeadAssignment.RuleId</c>. A
    /// source that is later archived or deleted leaves a dangling id here, and readers must
    /// degrade gracefully rather than assume it resolves.
    ///
    /// Server-set only. It is never bound from an HTTP request body — see the note on
    /// <c>CaptureLeadCommand.LeadSourceConfigId</c> for why.
    /// </summary>
    public Guid? LeadSourceConfigId { get; private set; }

    public LeadChannel? Channel { get; private set; }
    public string? Campaign { get; private set; }
    public string? ExternalReference { get; private set; }
    public string? NationalId { get; private set; }
    public string? CustomerReference { get; private set; }
    public string? Comment { get; private set; }
    public DateTimeOffset CapturedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }

    // ── Product & qualification ──────────────────────────────────────────────
    public string InterestedProduct { get; private set; } = default!;
    public Money? DesiredAmount { get; private set; }
    public string PreferredLanguage { get; private set; } = default!;
    public double QualificationCompleteness { get; private set; }

    // ── Scoring & intent ─────────────────────────────────────────────────────
    public int Score { get; private set; }
    public LeadIntentLevel IntentLevel { get; private set; } = LeadIntentLevel.Unknown;

    // ── Location & preferences ───────────────────────────────────────────────
    public GeoPoint? Location { get; private set; }
    public Guid? PreferredAgencyId { get; private set; }

    // ── Ownership & assignment ───────────────────────────────────────────────
    //
    // Three ids that answer three different questions, and whose names do not make that obvious:
    //   OwnerId             → who is responsible for the relationship (a user)
    //   CurrentAssignmentId → which dispatch record is in force      (a LeadAssignment row)
    //   CurrentAssignedId   → who that record names                 (a user, copied from it)
    //
    // Every combination is legal and meaningful: an owner with no assignment (captured with an
    // owner, never dispatched), an assignment with no owner (routed by the engine, nobody named
    // responsible), or both pointing at different people (a manager owns the relationship, an
    // agent works the current task).

    /// <summary>
    /// Lead Owner — the commercial responsible for the relationship. A management decision, not a
    /// routing one: it comes from the capture (an import column, a counter entry) or from
    /// <see cref="SetOwner"/> via <c>UpdateLeadOwnerCommand</c> / <c>BulkAssignOwnerCommand</c>,
    /// every change is mirrored into <see cref="LeadOwnerAssignmentHistory"/>, and
    /// <c>ListLeads</c> filters on it.
    ///
    /// <para>
    /// Dispatching NEVER writes this — deliberately. <c>LeadAutoDispatchConsumer</c> skips a lead
    /// whose capture already named an owner (<c>LeadCapturedEvent.HasExplicitOwner</c>), because
    /// auto-dispatch must not overrule a human decision and, after the fact, nothing can tell
    /// whether this field came from the capture or from a later assignment.
    /// </para>
    /// </summary>
    public Guid? OwnerId { get; private set; }

    public Guid? AgencyId { get; private set; }

    /// <summary>
    /// The agent named by <see cref="CurrentAssignmentId"/>'s row — a denormalised copy of
    /// <c>LeadAssignment.AgentId</c>, so the read side never has to join: the <c>ListLeads</c>
    /// projection, <c>GetAgentPerformance</c>'s grouping and <c>LeadsModuleFacade</c> all read it
    /// directly. A user id of the Administration module, hence no foreign key — another schema.
    ///
    /// <para>
    /// It is NOT the owner, and one letter is all that separates it from
    /// <see cref="CurrentAssignmentId"/> while the two point at different tables; both being
    /// <c>Guid?</c>, reading one for the other compiles. It moves as a pair with that field —
    /// <see cref="AssignTo"/> sets both, <see cref="ReturnToQueue"/> clears both, and nothing else
    /// may touch either.
    /// </para>
    /// </summary>
    public Guid? CurrentAssignedId { get; private set; }

    /// <summary>
    /// Capture-only provenance: the field agent who collected this lead. Never updated afterwards,
    /// and unrelated to ownership or to dispatching.
    /// </summary>
    public Guid? AgentCollectedLeadId { get; private set; }

    /// <summary>
    /// The <see cref="LeadAssignment"/> currently in force — the work order rather than the person:
    /// the strategy that picked the agent, the compatibility score and its factors, the rule that
    /// produced it, the SLA deadline, the first contact, and whether a supervisor overrode the
    /// engine. <c>RecordFirstContactHandler</c> and <c>GetNextActionHandler</c> join through it for
    /// exactly those.
    ///
    /// <para>
    /// Null means "not currently assigned", which is also what puts the lead back in the dispatch
    /// queue (<see cref="ReturnToQueue"/>). Non-null is the idempotency key of auto-dispatch: a
    /// replayed <c>LeadCapturedEvent</c> finds it set and the consumer stops, which is exact
    /// because the assignment and the lead are written in the same transaction.
    /// </para>
    ///
    /// <para>
    /// Only the CURRENT one. Previous assignments are not lost — they remain as
    /// <see cref="LeadAssignment"/> rows, which is what the assignment history reads.
    /// </para>
    /// </summary>
    public Guid? CurrentAssignmentId { get; private set; }

    // ── Attribution & UTM (F13.37-BE-04) ───────────────────────────────────
    public string? UtmSource { get; private set; }
    public string? UtmMedium { get; private set; }
    public string? UtmCampaign { get; private set; }
    public string? UtmContent { get; private set; }
    public string? UtmTerm { get; private set; }
    public string? LandingPage { get; private set; }
    public string? Referrer { get; private set; }
    public string? ExternalId { get; private set; }
    public string? SocialPublicationId { get; private set; }
    public string? SocialInteractionId { get; private set; }

    /// <summary>Snapshot of the source's CostPerLead at capture time. Never recalculated.</summary>
    public Money? AcquisitionCost { get; private set; }

    /// <summary>True for leads captured from a source in Testing status. Not dispatched.</summary>
    public bool IsTest { get; private set; }

    // ── Lifecycle tracking ───────────────────────────────────────────────────
    public DateTimeOffset? LastActivityAt { get; private set; }
    public string? LossReason { get; private set; }
    public DateTimeOffset? ConvertedAt { get; private set; }
    public Guid? ConvertedToCustomerId { get; private set; }

    private Lead() { } // EF Core

    // ── Factory ─────────────────────────────────────────────────────────────

    public static Lead Capture(
        Guid tenantId,
        string fullName,
        string phoneNumber,
        LeadSource source,
        string interestedProduct,
        string preferredLanguage,
        GeoPoint location,
        Guid? preferredAgencyId,
        TimeProvider clock,
        TimeSpan? lifetime = null,
        string? firstName = null,
        string? lastName = null,
        string? email = null,
        LeadGender gender = LeadGender.Unknown,
        DateOnly? dateOfBirth = null,
        Money? desiredAmount = null,
        string? campaign = null,
        LeadChannel? channel = null,
        string? comment = null,
        string? externalReference = null,
        Guid? ownerId = null,
        Guid? agencyId = null,
        Guid? agentCollectedLeadId = null,
        LeadType prospectType = LeadType.Individual,
        string? nationalId = null,
        string? customerReference = null,
        string? phoneBlindIndex = null,
        string? utmSource = null,
        string? utmMedium = null,
        string? utmCampaign = null,
        string? utmContent = null,
        string? utmTerm = null,
        string? landingPage = null,
        string? referrer = null,
        string? externalId = null,
        string? socialPublicationId = null,
        string? socialInteractionId = null,
        Money? acquisitionCost = null,
        bool isTest = false,
        Guid? leadSourceConfigId = null)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            throw new DomainException("Lead must have a name.");
        if (string.IsNullOrWhiteSpace(phoneNumber))
            throw new DomainException("Lead must have a phone number.");

        var now = clock.GetUtcNow();

        var lead = new Lead
        {
            Id                    = Guid.NewGuid(),
            TenantId              = tenantId,
            FullName              = fullName.Trim(),
            FirstName             = firstName?.Trim(),
            LastName              = lastName?.Trim(),
            PhoneNumber           = phoneNumber.Trim(),
            PhoneBlindIndex       = phoneBlindIndex,
            Email                 = email?.Trim(),
            Gender                = gender,
            DateOfBirth           = dateOfBirth,
            Source                = source,
            Channel               = channel,
            Campaign              = campaign?.Trim(),
            ExternalReference     = externalReference?.Trim(),
            Comment               = comment?.Trim(),
            InterestedProduct     = interestedProduct,
            DesiredAmount         = desiredAmount,
            PreferredLanguage     = preferredLanguage ?? "FR",
            Location              = location,
            PreferredAgencyId     = preferredAgencyId,
            AgencyId              = agencyId,
            OwnerId               = ownerId,
            AgentCollectedLeadId  = agentCollectedLeadId,
            ProspectType          = prospectType,
            NationalId            = nationalId?.Trim(),
            CustomerReference     = customerReference?.Trim(),
            Status                = LeadStatus.New,
            PipelineStage         = Domain.PipelineStage.New,
            IntentLevel           = LeadIntentLevel.Unknown,
            Score                 = 0,
            QualificationCompleteness = 0,
            CapturedAt            = now,
            CreatedAt             = now,
            UpdatedAt             = now,
            ExpiresAt             = now.Add(lifetime ?? TimeSpan.FromDays(180)),
            Website = "",
            CompanyEmail = "",
            CompanyName = "",
            CompanyPhone = "",
            UtmSource             = utmSource?.Trim(),
            UtmMedium             = utmMedium?.Trim(),
            UtmCampaign           = utmCampaign?.Trim(),
            UtmContent            = utmContent?.Trim(),
            UtmTerm               = utmTerm?.Trim(),
            LandingPage           = landingPage?.Trim(),
            Referrer              = referrer?.Trim(),
            ExternalId            = externalId?.Trim(),
            SocialPublicationId   = socialPublicationId?.Trim(),
            SocialInteractionId   = socialInteractionId?.Trim(),
            AcquisitionCost       = acquisitionCost,
            IsTest                = isTest,
            LeadSourceConfigId    = leadSourceConfigId,
        };

        lead.RaiseDomainEvent(new LeadCapturedDomainEvent(lead.Id));
        return lead;
    }

    /// <summary>Historical qualification threshold; see <see cref="Qualify"/>.</summary>
    public const int DefaultQualifiedThreshold = 60;

    /// <summary>
    /// A lead can be dispatched unless it has reached a terminal status. One predicate, so the
    /// domain and <c>DispatchLeadHandler</c> cannot disagree on what "dispatchable" means.
    /// </summary>
    public bool IsDispatchable =>
        Status is not (LeadStatus.Converted or LeadStatus.Lost
                    or LeadStatus.Disqualified or LeadStatus.Archived);

    // ── Status transitions ──────────────────────────────────────────────────

    /// <summary>Moves Lead from New to Open (first manual or system action).</summary>
    public Result Open()
    {
        if (Status != LeadStatus.New)
            return Result.Fail("Only a New lead can be opened.");

        Status    = LeadStatus.Open;
        UpdatedAt = DateTimeOffset.UtcNow;
        RaiseDomainEvent(new LeadStatusChangedDomainEvent(Id, LeadStatus.New, Status));
        return Result.Ok();
    }

    /// <summary>
    /// Qualifies the lead with a 0-100 score.
    /// Leads scoring ≥ 60 become Qualified (eligible for dispatching);
    /// below that they enter Qualifying for further work.
    /// </summary>
    /// <param name="qualifiedThreshold">
    /// Score at or above which the lead becomes <see cref="LeadStatus.Qualified"/> rather than
    /// <see cref="LeadStatus.Qualifying"/>. Configurable because 60 was calibrated for
    /// qualification AFTER interactions: <c>LeadScoreCalculator</c> awards 35 of its 100 points
    /// from activity history, so a lead scored at capture can never reach 60 (45 at most when it
    /// came from a file import, whose source quality is 5/20). A tenant that wants captured leads
    /// dispatched lowers this; everyone else keeps the historical 60.
    /// </param>
    public Result Qualify(int score, int qualifiedThreshold = DefaultQualifiedThreshold)
    {
        if (Status is LeadStatus.Converted or LeadStatus.Archived or LeadStatus.Lost or LeadStatus.Disqualified)
            return Result.Fail("LEAD_CANNOT_BE_QUALIFIED_FROM_CURRENT_STATUS");

        if (score is < 0 or > 100)
            return Result.Fail("SCORE_OUT_OF_RANGE");

        Score     = score;
        Status    = score >= qualifiedThreshold ? LeadStatus.Qualified : LeadStatus.Qualifying;
        UpdatedAt = DateTimeOffset.UtcNow;

        RaiseDomainEvent(new LeadQualifiedDomainEvent(Id, Status, score));
        return Result.Ok();
    }

    /// <summary>
    /// Records a dispatching assignment. Any lead that is still live may be dispatched — a
    /// captured lead is routed to an agent precisely so that someone qualifies it. Only the
    /// terminal statuses refuse: there is nobody to work a lead that is already converted, lost,
    /// disqualified or archived.
    /// </summary>
    /// <param name="currentAssignment">
    /// The assignment this one replaces, loaded by the caller, or <c>null</c> when the lead has
    /// none. It is a REQUIRED parameter rather than an optional one, and a mismatch is refused:
    /// the row has to be closed here, and this aggregate holds no navigation to reach it by
    /// itself. See <see cref="CloseCurrentAssignment"/> for what goes wrong when it is not.
    /// </param>
    public Result AssignTo(LeadAssignment assignment, LeadAssignment? currentAssignment)
    {
        if (!IsDispatchable)
            return Result.Fail("LEAD_NOT_DISPATCHABLE");

        var closing = CloseCurrentAssignment(currentAssignment);
        if (closing.IsFailure)
            return closing;

        CurrentAssignmentId = assignment.Id;
        CurrentAssignedId   = assignment.AgentId;
        UpdatedAt           = DateTimeOffset.UtcNow;

        RaiseDomainEvent(new LeadAssignedDomainEvent(Id, assignment.Id, assignment.AgentId));
        return Result.Ok();
    }

    /// <summary>Reverts to Qualified so the lead re-enters the dispatching queue.</summary>
    /// <param name="currentAssignment">
    /// The assignment being given up, loaded by the caller. Required for the same reason as in
    /// <see cref="AssignTo"/>: leaving it open is what used to keep alerting its agent.
    /// </param>
    public Result ReturnToQueue(LeadAssignment? currentAssignment)
    {
        if (CurrentAssignmentId is null)
            return Result.Fail("LEAD_IS_NOT_CURRENTLY_ASSIGNED");

        var closing = CloseCurrentAssignment(currentAssignment);
        if (closing.IsFailure)
            return closing;

        CurrentAssignmentId = null;
        CurrentAssignedId   = null;
        Status              = LeadStatus.Qualified;
        UpdatedAt           = DateTimeOffset.UtcNow;
        return Result.Ok();
    }

    /// <summary>
    /// Stamps the outgoing assignment as superseded, so exactly one row of a lead is ever open.
    ///
    /// <para>
    /// The caller has to hand the row over because <c>LeadAssignment</c> is mapped with
    /// <c>HasOne&lt;Lead&gt;().WithMany()</c> and no navigation property: this aggregate can see
    /// the id of its current assignment and nothing else. Rather than let a caller silently skip
    /// the step — which is precisely the bug this closes, a replaced row alerting its former agent
    /// every day through <c>CheckSlaBreachesJob</c> — a lead that HAS a current assignment and is
    /// handed <c>null</c> is refused with <c>CURRENT_ASSIGNMENT_REQUIRED</c>, and a row that is not
    /// the current one with <c>CURRENT_ASSIGNMENT_MISMATCH</c>.
    /// </para>
    /// </summary>
    private Result CloseCurrentAssignment(LeadAssignment? currentAssignment)
    {
        if (CurrentAssignmentId is null)
            return Result.Ok();

        if (currentAssignment is null)
            return Result.Fail("CURRENT_ASSIGNMENT_REQUIRED");

        if (currentAssignment.Id != CurrentAssignmentId.Value)
            return Result.Fail("CURRENT_ASSIGNMENT_MISMATCH");

        currentAssignment.Supersede(DateTimeOffset.UtcNow);
        return Result.Ok();
    }

    /// <summary>Sets the Lead Owner (commercial responsible).</summary>
    public Result SetOwner(Guid ownerId)
    {
        if (Status is LeadStatus.Converted or LeadStatus.Archived)
            return Result.Fail("Cannot change owner of a closed lead.");

        var previous = OwnerId;
        OwnerId   = ownerId;
        UpdatedAt = DateTimeOffset.UtcNow;
        RaiseDomainEvent(new LeadOwnerChangedDomainEvent(Id, previous, ownerId));
        return Result.Ok();
    }

    /// <summary>Advances the pipeline stage, enforcing the allow-list of forward transitions.</summary>
    public Result AdvancePipelineStage(PipelineStage newStage)
    {
        if (Status is LeadStatus.Converted or LeadStatus.Lost or LeadStatus.Disqualified or LeadStatus.Archived)
            return Result.Fail("Cannot move pipeline stage on a closed lead.");

        var previous  = PipelineStage;
        PipelineStage = newStage;
        UpdatedAt     = DateTimeOffset.UtcNow;
        RaiseDomainEvent(new LeadPipelineStageChangedDomainEvent(Id, previous, newStage));
        return Result.Ok();
    }

    /// <summary>Updates mutable lead information fields.</summary>
    public Result Update(
        string? fullName,
        string? firstName,
        string? lastName,
        string? email,
        LeadGender? gender,
        DateOnly? dateOfBirth,
        string? interestedProduct,
        Money? desiredAmount,
        string? preferredLanguage,
        string? campaign,
        string? comment,
        GeoPoint? location,
        Guid? preferredAgencyId)
    {
        if (Status is LeadStatus.Converted or LeadStatus.Archived)
            return Result.Fail("Cannot update a closed lead.");

        if (fullName is not null)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return Result.Fail("Full name cannot be blank.");
            FullName = fullName.Trim();
        }

        if (firstName is not null) FirstName = firstName.Trim();
        if (lastName  is not null) LastName  = lastName.Trim();
        if (email     is not null) Email     = email.Trim();
        if (gender    is not null) Gender    = gender.Value;
        if (dateOfBirth      is not null) DateOfBirth      = dateOfBirth;
        if (interestedProduct is not null) InterestedProduct = interestedProduct;
        if (desiredAmount    is not null) DesiredAmount    = desiredAmount;
        if (preferredLanguage is not null) PreferredLanguage = preferredLanguage;
        if (campaign  is not null) Campaign  = campaign.Trim();
        if (comment   is not null) Comment   = comment.Trim();
        if (location  is not null) Location  = location;
        if (preferredAgencyId is not null) PreferredAgencyId = preferredAgencyId;

        UpdatedAt = DateTimeOffset.UtcNow;
        return Result.Ok();
    }

    /// <summary>
    /// Updates the score in-place without changing lead status.
    /// Used for background auto-recalculations triggered by activity or data changes.
    /// </summary>
    internal void RecordScoreUpdate(int score)
    {
        Score     = Math.Clamp(score, 0, 100);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Updates the intent level (Hot/Warm/Cold/Unknown).</summary>
    public void UpdateIntentLevel(LeadIntentLevel level)
    {
        IntentLevel = level;
        UpdatedAt   = DateTimeOffset.UtcNow;
    }

    /// <summary>Records that an activity has been performed, refreshing LastActivityAt.</summary>
    public void RecordActivity()
    {
        LastActivityAt = DateTimeOffset.UtcNow;
        UpdatedAt      = DateTimeOffset.UtcNow;
    }

    /// <summary>Updates the qualification completeness percentage (0–1).</summary>
    public void SetQualificationCompleteness(double completeness)
    {
        QualificationCompleteness = Math.Clamp(completeness, 0, 1);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Closes the lead as Lost, Disqualified, or Archived.</summary>
    public Result Close(LeadCloseReason reason, string? detail = null)
    {
        if (Status is LeadStatus.Converted or LeadStatus.Archived or LeadStatus.Lost or LeadStatus.Disqualified)
            return Result.Fail("Lead is already closed.");

        var previous = Status;
        Status = reason switch
        {
            LeadCloseReason.Lost         => LeadStatus.Lost,
            LeadCloseReason.Disqualified => LeadStatus.Disqualified,
            LeadCloseReason.Archived     => LeadStatus.Archived,
            _                            => LeadStatus.Lost
        };

        LossReason = detail;
        UpdatedAt  = DateTimeOffset.UtcNow;
        RaiseDomainEvent(new LeadStatusChangedDomainEvent(Id, previous, Status));
        return Result.Ok();
    }

    /// <summary>Marks the lead as converted to a customer.</summary>
    public Result Convert(Guid customerId)
    {
        if (Status is LeadStatus.Lost or LeadStatus.Disqualified or LeadStatus.Archived)
            return Result.Fail("Cannot convert a closed lead.");

        var previous              = Status;
        Status                    = LeadStatus.Converted;
        PipelineStage             = Domain.PipelineStage.Converted;
        ConvertedAt               = DateTimeOffset.UtcNow;
        ConvertedToCustomerId     = customerId;
        UpdatedAt                 = DateTimeOffset.UtcNow;

        RaiseDomainEvent(new LeadStatusChangedDomainEvent(Id, previous, Status));
        RaiseDomainEvent(new LeadConvertedDomainEvent(Id, customerId));
        return Result.Ok();
    }

    /// <summary>Legacy overload kept for DispatchLead backward compatibility.</summary>
    public Result Convert()
    {
        if (Status is LeadStatus.Lost or LeadStatus.Disqualified or LeadStatus.Archived)
            return Result.Fail("ONLY_ASSIGNED_LEADS_CAN_BE_CONVERTED");

        var previous  = Status;
        Status        = LeadStatus.Converted;
        PipelineStage = Domain.PipelineStage.Converted;
        ConvertedAt   = DateTimeOffset.UtcNow;
        UpdatedAt     = DateTimeOffset.UtcNow;

        RaiseDomainEvent(new LeadStatusChangedDomainEvent(Id, previous, Status));
        return Result.Ok();
    }

    /// <summary>Marks the lead as Lost (legacy helper, delegates to Close).</summary>
    public Result MarkLost(string reason) => Close(LeadCloseReason.Lost, reason);

    /// <summary>
    /// Applies field values from the source lead onto this (target) lead during a merge.
    /// Only fields with a non-null source value AND whose corresponding preference flag
    /// is true are overwritten. Call before <see cref="MergeInto"/> on the source.
    /// </summary>
    internal void ApplyMergeOverrides(
        string? email             = null,
        string? firstName         = null,
        string? lastName          = null,
        string? nationalId        = null,
        string? customerReference = null,
        DateOnly? dateOfBirth     = null,
        LeadGender? gender        = null,
        Money? desiredAmount      = null,
        string? comment           = null,
        string? companyName       = null,
        string? companyEmail      = null,
        string? companyPhone      = null,
        string? website           = null,
        GeoPoint? location        = null)
    {
        if (email             is not null) Email             = email;
        if (firstName         is not null) FirstName         = firstName;
        if (lastName          is not null) LastName          = lastName;
        if (nationalId        is not null) NationalId        = nationalId;
        if (customerReference is not null) CustomerReference = customerReference;
        if (dateOfBirth.HasValue)          DateOfBirth       = dateOfBirth;
        if (gender.HasValue)               Gender            = gender.Value;
        if (desiredAmount     is not null) DesiredAmount     = desiredAmount;
        if (comment           is not null) Comment           = comment;
        if (companyName       is not null) CompanyName       = companyName;
        if (companyEmail      is not null) CompanyEmail      = companyEmail;
        if (companyPhone      is not null) CompanyPhone      = companyPhone;
        if (website           is not null) Website           = website;
        if (location          is not null) Location          = location;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Archives this lead as the source of a merge, raising a structured domain event
    /// so that the merge appears in the audit trail and timeline.
    /// </summary>
    public Result MergeInto(Guid targetLeadId, Guid mergedBy)
    {
        var closeResult = Close(
            LeadCloseReason.Archived,
            $"Merged into lead {targetLeadId} by {mergedBy}");

        if (closeResult.IsFailure)
            return closeResult;

        RaiseDomainEvent(new Events.LeadMergedDomainEvent(Id, targetLeadId, mergedBy));
        return Result.Ok();
    }

    /// <summary>
    /// Raises <see cref="Events.DuplicateDismissedDomainEvent"/> on this lead so the
    /// timeline interceptor can dispatch it after the SaveChanges commit.
    /// Called by <c>DismissDuplicateHandler</c> since <see cref="DuplicateDismissal"/>
    /// is not an aggregate root and cannot raise domain events itself.
    /// </summary>
    internal void RaiseDismissalEvent(Guid candidateLeadId, Guid dismissedBy)
        => RaiseDomainEvent(new Events.DuplicateDismissedDomainEvent(Id, candidateLeadId, dismissedBy));

    /// <summary>Raises <see cref="Events.ConsentRecordedDomainEvent"/>. Called by <c>RecordConsentHandler</c>.</summary>
    internal void RaiseConsentRecordedEvent(Guid consentId, string consentType)
        => RaiseDomainEvent(new Events.ConsentRecordedDomainEvent(Id, consentId, consentType));

    /// <summary>Raises <see cref="Events.ConsentWithdrawnDomainEvent"/>. Called by <c>WithdrawConsentHandler</c>.</summary>
    internal void RaiseConsentWithdrawnEvent(Guid consentId, string consentType)
        => RaiseDomainEvent(new Events.ConsentWithdrawnDomainEvent(Id, consentId, consentType));

    /// <summary>Raises <see cref="Events.SlaEscalatedDomainEvent"/>. Called by <c>CheckSlaBreachesJob</c>.</summary>
    internal void RaiseSlaEscalatedEvent(Guid assignmentId, Guid agentId, int escalationDepth)
        => RaiseDomainEvent(new Events.SlaEscalatedDomainEvent(Id, assignmentId, agentId, escalationDepth));

    /// <summary>
    /// Transitions a New lead to Open — signals the first manual or system
    /// action has been taken on this lead.
    /// </summary>
    public Result Reopen()
    {
        if (Status != LeadStatus.New)
            return Result.Fail("Only a New lead can be opened.");

        var previous = Status;
        Status    = LeadStatus.Open;
        UpdatedAt = DateTimeOffset.UtcNow;
        RaiseDomainEvent(new LeadStatusChangedDomainEvent(Id, previous, Status));
        return Result.Ok();
    }

    /// <summary>
    /// Moves an active lead into the Nurturing state — used when a lead is
    /// not yet ready to be qualified but should be kept warm with regular
    /// touchpoints.
    /// </summary>
    public Result Nurture()
    {
        if (Status is LeadStatus.Converted or LeadStatus.Lost
                   or LeadStatus.Disqualified or LeadStatus.Archived)
            return Result.Fail("LEAD_CANNOT_BE_NURTURED_FROM_CURRENT_STATUS");

        if (Status == LeadStatus.Nurturing)
            return Result.Fail("Lead is already in Nurturing.");

        var previous = Status;
        Status    = LeadStatus.Nurturing;
        UpdatedAt = DateTimeOffset.UtcNow;
        RaiseDomainEvent(new LeadStatusChangedDomainEvent(Id, previous, Status));
        return Result.Ok();
    }

    /// <summary>
    /// Recycles a closed or nurturing lead back into the active pipeline.
    /// Resets the score to 0 so the lead must be re-qualified.
    /// </summary>
    public Result Recycle(LeadSource? newSource = null, string? newCampaign = null)
    {
        if (Status is not (LeadStatus.Lost or LeadStatus.Disqualified or LeadStatus.Nurturing))
            return Result.Fail("Only Lost, Disqualified or Nurturing leads can be recycled.");

        var previous = Status;
        Status = LeadStatus.Recycled;

        if (newSource.HasValue) Source = newSource.Value;
        if (newCampaign is not null) Campaign = newCampaign.Trim();

        // Reset score: the lead must go through qualification again.
        Score     = 0;
        UpdatedAt = DateTimeOffset.UtcNow;
        RaiseDomainEvent(new LeadStatusChangedDomainEvent(Id, previous, Status));
        return Result.Ok();
    }

    /// <summary>
    /// Reactivates a Recycled lead back into the Open state so it re-enters
    /// the qualification/scoring pipeline (US-M13-161).
    /// </summary>
    public Result Reactivate()
    {
        if (Status != LeadStatus.Recycled)
            return Result.Fail("ONLY_RECYCLED_LEADS_CAN_BE_REACTIVATED");

        var previous = Status;
        Status    = LeadStatus.Open;
        UpdatedAt = DateTimeOffset.UtcNow;
        RaiseDomainEvent(new LeadStatusChangedDomainEvent(Id, previous, Status));
        return Result.Ok();
    }
}

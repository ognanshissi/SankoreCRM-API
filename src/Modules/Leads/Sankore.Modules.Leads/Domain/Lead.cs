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
    /// <summary>Lead Owner — the commercial responsible. Distinct from the agent assigned to a task.</summary>
    public Guid? OwnerId { get; private set; }
    public Guid? AgencyId { get; private set; }
    public Guid? CurrentAssignedId { get; private set; }
    public Guid? AgentCollectedLeadId { get; private set; }
    public Guid? CurrentAssignmentId { get; private set; }

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
        string? customerReference = null)
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
            PreferredLanguage     = preferredLanguage,
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
            ExpiresAt             = now.Add(lifetime ?? TimeSpan.FromDays(180))
        };

        lead.RaiseDomainEvent(new LeadCapturedDomainEvent(lead.Id));
        return lead;
    }

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
    public Result Qualify(int score)
    {
        if (Status is LeadStatus.Converted or LeadStatus.Archived or LeadStatus.Lost or LeadStatus.Disqualified)
            return Result.Fail("LEAD_CANNOT_BE_QUALIFIED_FROM_CURRENT_STATUS");

        if (score is < 0 or > 100)
            return Result.Fail("SCORE_OUT_OF_RANGE");

        Score     = score;
        Status    = score >= 60 ? LeadStatus.Qualified : LeadStatus.Qualifying;
        UpdatedAt = DateTimeOffset.UtcNow;

        RaiseDomainEvent(new LeadQualifiedDomainEvent(Id, Status, score));
        return Result.Ok();
    }

    /// <summary>Assigns the lead to an agent via a freshly created LeadAssignment.</summary>
    public Result AssignTo(LeadAssignment assignment)
    {
        if (Status != LeadStatus.Qualified)
            return Result.Fail("ONLY_QUALIFIED_LEADS_CAN_BE_DISPATCHED");

        CurrentAssignmentId = assignment.Id;
        CurrentAssignedId   = assignment.AgentId;
        UpdatedAt           = DateTimeOffset.UtcNow;

        RaiseDomainEvent(new LeadAssignedDomainEvent(Id, assignment.Id, assignment.AgentId));
        return Result.Ok();
    }

    /// <summary>Reverts to Qualified so the lead re-enters the dispatching queue.</summary>
    public Result ReturnToQueue()
    {
        if (CurrentAssignmentId is null)
            return Result.Fail("LEAD_IS_NOT_CURRENTLY_ASSIGNED");

        CurrentAssignmentId = null;
        CurrentAssignedId   = null;
        Status              = LeadStatus.Qualified;
        UpdatedAt           = DateTimeOffset.UtcNow;
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
}

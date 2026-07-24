namespace Sankore.Modules.Leads.Domain;

using Sankore.Modules.Leads.Domain.Events;
using Sankore.Shared.Kernel;

/// <summary>
/// Aggregate root for a prospect prior to conversion into a Customer
/// (module M01). Every state transition is expressed as a method that
/// enforces its own invariants and raises a domain event — no other code,
/// including EF Core, is allowed to set these properties directly (hence
/// private setters and a parameterless private constructor for EF only).
/// </summary>
public sealed class Lead: AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string FullName { get; private set; } = default!;
    public string PhoneNumber { get; private set; } = default!;
    public string Website { get; private set; } = default!;
    public string CompanyName { get; private set; } = default!;
    public string CompanyEmail { get; private set; } = default!;
    public string CompanyPhone { get; private set; } = default!;
    public string CompanyAddress { get; private set; } = default!;
    public LeadStatus Status { get; private set; }
    public LeadSource Source { get; private set; }
    public string InterestedProduct { get; private set; } = default!;
    public string PreferredLanguage { get; private set; } = default!;
    public GeoPoint? Location { get; private set; }
    public Guid? PreferredAgencyId { get; private set; }
    public int Score { get; private set; }
    public Guid? CurrentAssignmentId { get; private set; }
    public string? LossReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }

    private Lead() { } // EF Core

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
        TimeSpan? lifetime = null)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            throw new DomainException("Lead must have a name.");
        if (string.IsNullOrWhiteSpace(phoneNumber))
            throw new DomainException("Lead must have a phone number.");

        var now = clock.GetUtcNow();

        var lead = new Lead
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            FullName = fullName.Trim(),
            PhoneNumber = phoneNumber.Trim(),
            Source = source,
            InterestedProduct = interestedProduct,
            PreferredLanguage = preferredLanguage,
            Location = location,
            PreferredAgencyId = preferredAgencyId,
            Status = LeadStatus.New,
            CreatedAt = now,
            ExpiresAt = now.Add(lifetime ?? TimeSpan.FromDays(180))
        };

        lead.RaiseDomainEvent(new LeadCapturedDomainEvent(lead.Id));
        return lead;
    }

    /// <summary>
    /// Qualifies the lead with a 0-100 score. Leads scoring 60+ become
    /// Sales Qualified (eligible for dispatching); below that they become
    /// Marketing Qualified and enter nurturing (F13.7) instead.
    /// </summary>
    public Result Qualify(int score)
    {
        if (Status is not (LeadStatus.New or LeadStatus.Contacted or LeadStatus.MarketingQualified))
            return Result.Fail("LEAD_CANNOT_BE_QUALIFIED_FROM_CURRENT_STATUS");

        if (score is < 0 or > 100)
            return Result.Fail("SCORE_OUT_OF_RANGE");

        Score = score;
        Status = score >= 60 ? LeadStatus.SalesQualified : LeadStatus.MarketingQualified;

        RaiseDomainEvent(new LeadQualifiedDomainEvent(Id, Status, score));
        return Result.Ok();
    }

    /// <summary>
    /// Assigns the lead to an agent via a freshly created LeadAssignment.
    /// Only Sales Qualified leads may be dispatched (F13.9 rule).
    /// </summary>
    public Result AssignTo(LeadAssignment assignment)
    {
        if (Status != LeadStatus.SalesQualified)
            return Result.Fail("ONLY_SQL_LEADS_CAN_BE_DISPATCHED");

        CurrentAssignmentId = assignment.Id;
        Status = LeadStatus.Assigned;

        RaiseDomainEvent(new LeadAssignedDomainEvent(Id, assignment.Id, assignment.AgentId));
        return Result.Ok();
    }

    /// <summary>
    /// Reverts a lead back to SalesQualified so it re-enters the dispatching
    /// queue — used when an agent refuses a lead (F13.13) or fails its SLA.
    /// </summary>
    public Result ReturnToQueue()
    {
        if (Status != LeadStatus.Assigned)
            return Result.Fail("LEAD_IS_NOT_CURRENTLY_ASSIGNED");

        CurrentAssignmentId = null;
        Status = LeadStatus.SalesQualified;
        return Result.Ok();
    }

    public Result MarkLost(string reason)
    {
        if (Status is LeadStatus.Converted or LeadStatus.Archived)
            return Result.Fail("LEAD_ALREADY_CLOSED");

        Status = LeadStatus.Lost;
        LossReason = reason;
        return Result.Ok();
    }

    public Result Convert()
    {
        if (Status != LeadStatus.Assigned)
            return Result.Fail("ONLY_ASSIGNED_LEADS_CAN_BE_CONVERTED");

        Status = LeadStatus.Converted;
        return Result.Ok();
    }
}

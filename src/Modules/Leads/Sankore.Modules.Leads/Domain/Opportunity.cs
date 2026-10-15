namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// A sales opportunity linked to a lead or existing customer (US-M13-130/131).
/// Tenant-scoped. Customer reference uses the opaque EntityType/EntityId
/// pattern — no physical FK when the Customer module lives in a separate schema.
/// </summary>
public sealed class Opportunity : AggregateRoot
{
    public Guid Id { get; private set; }

    /// <summary>Lead that originated this opportunity (null when created for an existing customer).</summary>
    public Guid? LeadId { get; private set; }

    // ── Cross-schema customer reference (opaque, no physical FK) ─────────
    /// <summary>"Customer" when linked to a Customer 360 entity; null when lead-only.</summary>
    public string? CustomerEntityType { get; private set; }
    /// <summary>Customer ID in the external module; null when lead-only.</summary>
    public Guid? CustomerEntityId { get; private set; }

    public string Title { get; private set; } = default!;
    public string? Description { get; private set; }
    public string Product { get; private set; } = default!;
    public Money? EstimatedAmount { get; private set; }
    public OpportunityStage Stage { get; private set; }
    public double Probability { get; private set; }
    public DateTimeOffset? ExpectedCloseDate { get; private set; }
    public Guid? OwnerId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public string? CloseReason { get; private set; }

    private Opportunity() { } // EF Core

    public static Opportunity CreateFromLead(
        Guid tenantId,
        Guid leadId,
        string title,
        string product,
        Guid? ownerId,
        TimeProvider clock,
        string? description = null,
        Money? estimatedAmount = null,
        DateTimeOffset? expectedCloseDate = null)
    {
        var now = clock.GetUtcNow();
        var opp = new Opportunity
        {
            Id                = Guid.NewGuid(),
            TenantId          = tenantId,
            LeadId            = leadId,
            Title             = title.Trim(),
            Description       = description?.Trim(),
            Product           = product.Trim(),
            EstimatedAmount   = estimatedAmount,
            Stage             = OpportunityStage.Prospecting,
            Probability       = 0.1,
            ExpectedCloseDate = expectedCloseDate,
            OwnerId           = ownerId,
            CreatedAt         = now,
            UpdatedAt         = now,
        };
        opp.RaiseDomainEvent(new Events.OpportunityCreatedDomainEvent(opp.Id, opp.LeadId, null));
        return opp;
    }

    public static Opportunity CreateForCustomer(
        Guid tenantId,
        Guid customerEntityId,
        string title,
        string product,
        Guid? ownerId,
        TimeProvider clock,
        Guid? leadId = null,
        string? description = null,
        Money? estimatedAmount = null,
        DateTimeOffset? expectedCloseDate = null)
    {
        var now = clock.GetUtcNow();
        var opp = new Opportunity
        {
            Id                 = Guid.NewGuid(),
            TenantId           = tenantId,
            LeadId             = leadId,
            CustomerEntityType = "Customer",
            CustomerEntityId   = customerEntityId,
            Title              = title.Trim(),
            Description        = description?.Trim(),
            Product            = product.Trim(),
            EstimatedAmount    = estimatedAmount,
            Stage              = OpportunityStage.Prospecting,
            Probability        = 0.1,
            ExpectedCloseDate  = expectedCloseDate,
            OwnerId            = ownerId,
            CreatedAt          = now,
            UpdatedAt          = now,
        };
        opp.RaiseDomainEvent(new Events.OpportunityCreatedDomainEvent(opp.Id, leadId, customerEntityId));
        return opp;
    }

    /// <summary>
    /// Moves the request to another stage.
    ///
    /// <para>
    /// <b>Any open stage to any open stage, in both directions.</b> A commercial pipeline is not a
    /// ladder: a deal that was in negotiation and goes back to needs analysis is an ordinary week,
    /// and a forward-only rule would have agents closing and re-creating requests to express it —
    /// which destroys the history the stage is there to keep.
    /// </para>
    ///
    /// <para>
    /// A CLOSED request refuses every move. Re-opening is not a transition but a decision with its
    /// own consequences — a won one may already have produced a draft contract — and nothing in
    /// this product asks for it yet.
    /// </para>
    /// </summary>
    public Result AdvanceStage(OpportunityStage newStage, DateTimeOffset now)
    {
        if (Stage == OpportunityStage.ClosedWon || Stage == OpportunityStage.ClosedLost)
            return Result.Fail("OPPORTUNITY_ALREADY_CLOSED");

        Stage       = newStage;
        Probability = StageProbability(newStage);
        UpdatedAt   = now;

        if (newStage is OpportunityStage.ClosedWon or OpportunityStage.ClosedLost)
            ClosedAt = now;

        return Result.Ok();
    }

    /// <summary>
    /// Closes the request, won or lost, with the motive the agent gave.
    ///
    /// <para>
    /// <b>The motive is written here and nowhere else.</b> It used to be accepted and silently
    /// dropped while the column, the DTO and both read handlers projected it — so every closed
    /// request answered <c>null</c> to "why", and the one field a later reviewer actually reads
    /// was the one nothing stored.
    /// </para>
    /// </summary>
    public Result Close(OpportunityStage outcome, DateTimeOffset now, string? reason = null)
    {
        if (outcome is not (OpportunityStage.ClosedWon or OpportunityStage.ClosedLost))
            return Result.Fail("INVALID_CLOSE_OUTCOME");

        var moved = AdvanceStage(outcome, now);
        if (moved.IsFailure)
            return moved;

        CloseReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        return Result.Ok();
    }

    /// <summary>
    /// Patches the editable fields. <b>Null leaves alone</b> — the screen posts only what it
    /// collected, so a null that cleared would make each save erase the last. Same rule as
    /// <c>Lead.EnrichPrincipalIdentity</c>.
    /// </summary>
    public void Update(
        string? title,
        string? description,
        Money? estimatedAmount,
        DateTimeOffset? expectedCloseDate,
        Guid? ownerId,
        DateTimeOffset now)
    {
        if (title is not null) Title = title.Trim();
        if (description is not null) Description = description.Trim();
        if (estimatedAmount is not null) EstimatedAmount = estimatedAmount;
        if (expectedCloseDate is not null) ExpectedCloseDate = expectedCloseDate;
        if (ownerId is not null) OwnerId = ownerId;
        UpdatedAt = now;
    }

    private static double StageProbability(OpportunityStage stage) => stage switch
    {
        OpportunityStage.Prospecting        => 0.10,
        OpportunityStage.Qualification      => 0.20,
        OpportunityStage.NeedsAnalysis      => 0.40,
        OpportunityStage.Proposal           => 0.60,
        OpportunityStage.Negotiation        => 0.80,
        OpportunityStage.ClosedWon          => 1.00,
        OpportunityStage.ClosedLost         => 0.00,
        _                                   => 0.10,
    };
}

public enum OpportunityStage
{
    Prospecting,
    Qualification,
    NeedsAnalysis,
    Proposal,
    Negotiation,
    ClosedWon,
    ClosedLost
}

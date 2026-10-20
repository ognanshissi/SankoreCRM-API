namespace Sankore.Modules.Leads.Features.Opportunities;

using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel.ValueObject;

/// <param name="Product">Empty while nobody has matched the need to a product.</param>
public sealed record OpportunityDto(
    Guid Id,
    Guid? LeadId,
    string? CustomerEntityType,
    Guid? CustomerEntityId,
    string Title,
    string? Description,
    string Product,
    Money? EstimatedAmount,
    OpportunityStage Stage,
    double Probability,
    DateTimeOffset? ExpectedCloseDate,
    Guid? OwnerId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    string? CloseReason,
    string? NextStep = null,
    DateOnly? NextStepDueOn = null,
    /// <param name="SeparateNeedConfirmedAt">
    /// When an agent said this request is a different need from the lead's, despite the amounts.
    /// </param>
    DateTimeOffset? SeparateNeedConfirmedAt = null);

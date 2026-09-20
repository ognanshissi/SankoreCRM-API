namespace Sankore.Modules.Leads.Features.Opportunities;

using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel.ValueObject;

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
    string? CloseReason);

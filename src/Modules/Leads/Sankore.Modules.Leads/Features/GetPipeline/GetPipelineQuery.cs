namespace Sankore.Modules.Leads.Features.GetPipeline;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record GetPipelineQuery(
    Guid? AgencyId = null,
    Guid? OwnerId = null,
    int MaxPerStage = 50
) : IRequest<Result<PipelineView>>;

/// <summary>Kanban board view: stages with their lead cards.</summary>
public sealed record PipelineView(IReadOnlyList<PipelineColumn> Columns);

public sealed record PipelineColumn(
    string Stage,
    int TotalCount,
    IReadOnlyList<PipelineCard> Cards);

public sealed record PipelineCard(
    Guid Id,
    string FullName,
    string? Email,
    string PhoneNumber,
    string InterestedProduct,
    int Score,
    string IntentLevel,
    Guid? OwnerId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastActivityAt,
    /// <summary>Echo back as expectedUpdatedAt when moving the card, to detect concurrent edits.</summary>
    DateTimeOffset UpdatedAt);

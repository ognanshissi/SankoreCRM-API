namespace Sankore.Modules.Leads.Features.LeadSources.ListRuns;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

internal sealed record ListRunsQuery(
    Guid SourceId,
    int Page = 1,
    int PageSize = 20
) : IRequest<Result<PagedResult<RunDto>>>;

public sealed record RunDto(
    Guid Id,
    LeadSourceRunType RunType,
    LeadSourceRunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int FetchedCount,
    int IngestedCount,
    int RejectedCount,
    int DuplicateCount,
    string? ErrorMessage);

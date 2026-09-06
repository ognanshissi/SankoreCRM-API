namespace Sankore.Api.Features.Audit.GetAuditEntries;

using MediatR;
using Sankore.Shared.Kernel;

public sealed record GetAuditEntriesQuery(
    Guid? UserId,
    string? Action,
    string? ResourceType,
    string? ResourceId,
    string? Outcome,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Page = 1,
    int PageSize = 20,
    bool SortAscending = false   // true = chronological (oldest first), for entity timeline views
) : IRequest<Result<PagedResult<AuditEntryDto>>>;

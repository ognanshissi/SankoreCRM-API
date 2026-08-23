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
    int PageSize = 20
) : IRequest<Result<PagedResult<AuditEntryDto>>>;

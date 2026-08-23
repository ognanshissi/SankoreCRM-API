namespace Sankore.Api.Features.Audit.GetAuditEntries;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Api.Infrastructure.Audit;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class GetAuditEntriesHandler(
    IDbContextFactory<AuditDbContext> factory,
    ICurrentUser currentUser
) : IRequestHandler<GetAuditEntriesQuery, Result<PagedResult<AuditEntryDto>>>
{
    public async Task<Result<PagedResult<AuditEntryDto>>> Handle(
        GetAuditEntriesQuery request,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var query = db.Entries.AsNoTracking()
            .Where(e => e.TenantId == currentUser.TenantId);

        if (request.UserId.HasValue)
            query = query.Where(e => e.UserId == request.UserId.Value);

        if (!string.IsNullOrWhiteSpace(request.Action))
            query = query.Where(e => e.Action == request.Action);

        if (!string.IsNullOrWhiteSpace(request.ResourceType))
            query = query.Where(e => e.ResourceType == request.ResourceType);

        if (!string.IsNullOrWhiteSpace(request.ResourceId))
            query = query.Where(e => e.ResourceId == request.ResourceId);

        if (!string.IsNullOrWhiteSpace(request.Outcome))
            query = query.Where(e => e.Outcome == request.Outcome);

        if (request.From.HasValue)
            query = query.Where(e => e.Timestamp >= request.From.Value);

        if (request.To.HasValue)
            query = query.Where(e => e.Timestamp <= request.To.Value);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(e => e.Timestamp)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(e => new AuditEntryDto(
                e.Id,
                e.Timestamp,
                e.UserId,
                e.TenantId,
                e.Action,
                e.Outcome,
                e.ErrorDetail,
                e.PayloadJson,
                e.ResourceType,
                e.ResourceId,
                e.IpAddress,
                e.CorrelationId))
            .ToListAsync(cancellationToken);

        return Result.Ok(new PagedResult<AuditEntryDto>(items, totalCount, request.Page, request.PageSize));
    }
}

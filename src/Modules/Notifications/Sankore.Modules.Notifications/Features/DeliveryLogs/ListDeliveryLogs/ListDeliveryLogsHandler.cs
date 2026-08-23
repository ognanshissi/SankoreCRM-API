namespace Sankore.Modules.Notifications.Features.DeliveryLogs.ListDeliveryLogs;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Notifications.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListDeliveryLogsHandler(NotificationsDbContext db)
    : IRequestHandler<ListDeliveryLogsQuery, Result<PagedDeliveryLogsResult>>
{
    public async Task<Result<PagedDeliveryLogsResult>> Handle(
        ListDeliveryLogsQuery request, CancellationToken ct)
    {
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var page = Math.Max(request.Page, 1);

        // Tenant query filter is applied automatically by NotificationsDbContext
        var query = db.EmailDeliveryLogs.AsQueryable();

        if (request.RecipientEmail is not null)
            query = query.Where(l => l.RecipientEmail == request.RecipientEmail);

        if (request.EventType is not null)
            query = query.Where(l => l.EventType == request.EventType.Value);

        if (request.OutboxMessageId is not null)
            query = query.Where(l => l.OutboxMessageId == request.OutboxMessageId.Value);

        if (request.From is not null)
            query = query.Where(l => l.RecordedAt >= request.From.Value);

        if (request.To is not null)
            query = query.Where(l => l.RecordedAt <= request.To.Value);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(l => l.RecordedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new DeliveryLogDto(
                l.Id,
                l.OutboxMessageId,
                l.EventType.ToString(),
                l.RecipientEmail,
                l.RecordedAt))
            .ToListAsync(ct);

        return Result<PagedDeliveryLogsResult>.Ok(
            new PagedDeliveryLogsResult(items, totalCount, page, pageSize));
    }
}

namespace Sankore.Modules.Notifications.Features.DeliveryLogs.ListDeliveryLogs;

using MediatR;
using Sankore.Modules.Notifications.Domain;
using Sankore.Shared.Kernel;

internal sealed record ListDeliveryLogsQuery(
    string? RecipientEmail = null,
    EmailDeliveryEventType? EventType = null,
    Guid? OutboxMessageId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50)
    : IRequest<Result<PagedResult<DeliveryLogDto>>>;

internal sealed record DeliveryLogDto(
    Guid Id,
    Guid? OutboxMessageId,
    string EventType,
    string RecipientEmail,
    DateTimeOffset RecordedAt);

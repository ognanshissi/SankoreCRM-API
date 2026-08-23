namespace Sankore.Modules.Notifications.Features.DeliveryLogs;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Features.DeliveryLogs.ListDeliveryLogs;
using Sankore.Shared.Kernel;

internal static class DeliveryLogsEndpoints
{
    internal static IEndpointRouteBuilder MapDeliveryLogsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("delivery-logs").WithTags("Delivery Logs");

        // GET /delivery-logs?recipientEmail=&eventType=&outboxMessageId=&from=&to=&page=&pageSize=
        group.MapGet("", async (
            string? recipientEmail,
            string? eventType,
            Guid? outboxMessageId,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int page = 1,
            int pageSize = 50,
            ISender sender = default!,
            CancellationToken ct = default) =>
        {
            EmailDeliveryEventType? parsedEventType = null;
            if (eventType is not null && Enum.TryParse<EmailDeliveryEventType>(eventType, ignoreCase: true, out var et))
                parsedEventType = et;

            var result = await sender.Send(
                new ListDeliveryLogsQuery(
                    recipientEmail, parsedEventType, outboxMessageId, from, to, page, pageSize),
                ct);

            return result.IsSuccess ? Results.Ok(result.Value) : Results.Problem(result.Error);
        })
        .RequireAuthorization(Permissions.CanReadEmailDeliveryLogs.Code);

        return app;
    }
}

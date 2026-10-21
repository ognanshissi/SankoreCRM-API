namespace Sankore.Modules.Notifications.Features.InApp;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Notifications.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;

/// <summary>
/// The bell (WA-FE-10): the signed-in user's own notifications. No permission beyond being signed
/// in, and no user parameter anywhere — the subject is always <see cref="ICurrentUser.Id"/>, so
/// nobody can read or acknowledge somebody else's.
/// </summary>
public static class InAppNotificationsEndpoints
{
    public static IEndpointRouteBuilder MapInAppNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("my-notifications").WithTags("Notifications").RequireAuthorization();

        group.MapGet("", ListAsync)
            .WithName("ListMyNotifications")
            .WithSummary("The signed-in user's notifications, newest first, with the unread count")
            .Produces<MyNotificationsDto>()
            .WithOpenApi();

        group.MapPost("{id:guid}/read", ReadAsync)
            .WithName("MarkMyNotificationRead")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        group.MapPost("read-all", ReadAllAsync)
            .WithName("MarkAllMyNotificationsRead")
            .Produces(StatusCodes.Status204NoContent)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> ListAsync(
        int? limit, bool? unreadOnly, NotificationsDbContext db, ICurrentUser me, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 20, 1, 100);

        var mine = db.UserNotifications.Where(n => n.UserId == me.Id);

        var unread = await mine.CountAsync(n => n.ReadAt == null, ct);

        var items = await mine
            .Where(n => unreadOnly != true || n.ReadAt == null)
            .OrderByDescending(n => n.CreatedAt)
            .Take(take)
            .Select(n => new MyNotificationDto(n.Id, n.Category, n.Title, n.Body, n.Link, n.CreatedAt, n.ReadAt))
            .ToListAsync(ct);

        return Results.Ok(new MyNotificationsDto(items, unread));
    }

    private static async Task<IResult> ReadAsync(
        Guid id, NotificationsDbContext db, ICurrentUser me, TimeProvider clock, CancellationToken ct)
    {
        var notification = await db.UserNotifications
            .AsTracking()
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == me.Id, ct);

        if (notification is null) return Results.NotFound();

        notification.MarkRead(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ReadAllAsync(
        NotificationsDbContext db, ICurrentUser me, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        await db.UserNotifications
            .Where(n => n.UserId == me.Id && n.ReadAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(n => n.ReadAt, now), ct);

        return Results.NoContent();
    }
}

/// <param name="Category">Dotted, e.g. <c>whatsapp.escalation</c> — picks the icon.</param>
/// <param name="Link">A CRM route to open, or null.</param>
public sealed record MyNotificationDto(
    Guid Id,
    string Category,
    string Title,
    string? Body,
    string? Link,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

public sealed record MyNotificationsDto(IReadOnlyList<MyNotificationDto> Items, int UnreadCount);

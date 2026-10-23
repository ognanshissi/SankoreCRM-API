using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;

namespace Sankore.Admin.Features.Support;

/// <summary>
/// Error codes of the support routes. Sankore.Api relays them to the tenant's front-end as they
/// are, so they are a contract: never rename one.
/// </summary>
public static class SupportErrors
{
    public const string TicketNotFound = "SUPPORT_TICKET_NOT_FOUND";
    public const string TicketClosed = "SUPPORT_TICKET_CLOSED";
    public const string InvalidTransition = "SUPPORT_INVALID_TRANSITION";
    public const string InvalidTicket = "INVALID_SUPPORT_TICKET";
    public const string InvalidMessage = "INVALID_SUPPORT_MESSAGE";
    public const string AssigneeNotFound = "SUPPORT_ASSIGNEE_NOT_FOUND";
    public const string InvalidActor = "INVALID_ACTOR";
    public const string TenantNotFound = "TENANT_NOT_FOUND";
}

/// <summary>What a support command answers; the endpoint translates it (<see cref="SupportHttp.ToHttp"/>).</summary>
public sealed record SupportResult(string? Error, Guid? TicketId = null, long? Number = null, string? Detail = null)
{
    public bool IsSuccess => Error is null;

    public static SupportResult Ok(Guid ticketId, long? number = null) => new(null, ticketId, number);
    public static SupportResult Fail(string error, string? detail = null) => new(error, null, null, detail);
}

public sealed record SupportMessageResponse(
    Guid Id,
    SupportMessageKind Kind,
    SupportAuthorKind AuthorKind,
    Guid? AuthorId,
    string AuthorName,
    string? Body,
    SupportTicketStatus? Status,
    DateTimeOffset CreatedAt)
{
    public static SupportMessageResponse From(SupportMessage m) =>
        new(m.Id, m.Kind, m.AuthorKind, m.AuthorId, m.AuthorName, m.Body, m.Status, m.CreatedAt);
}

/// <summary>A ticket as a list row shows it, either side.</summary>
public sealed record SupportTicketSummary(
    Guid Id,
    long Number,
    string Reference,
    Guid TenantId,
    string Subject,
    SupportTicketCategory Category,
    SupportTicketPriority Priority,
    SupportTicketStatus Status,
    Guid RequesterUserId,
    string RequesterName,
    string RequesterEmail,
    Guid? AssignedAdminId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt)
{
    public static SupportTicketSummary From(SupportTicket t) => new(
        t.Id, t.Number, t.Reference, t.TenantId, t.Subject, t.Category, t.Priority, t.Status,
        t.RequesterUserId, t.RequesterName, t.RequesterEmail, t.AssignedAdminId, t.CreatedAt, t.LastActivityAt);
}

/// <summary>A ticket with its thread. The tenant routes never put an internal note in it.</summary>
public sealed record SupportTicketDetail(
    SupportTicketSummary Ticket,
    string? RequesterRole,
    string? PageUrl,
    string? AppVersion,
    string? UserAgent,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<SupportMessageResponse> Messages)
{
    public static SupportTicketDetail From(SupportTicket t, bool includeInternal) => new(
        SupportTicketSummary.From(t), t.RequesterRole, t.PageUrl, t.AppVersion, t.UserAgent, t.ResolvedAt, t.ClosedAt,
        t.Messages
            .Where(m => includeInternal || m.IsVisibleToTenant)
            .OrderBy(m => m.CreatedAt)
            .Select(SupportMessageResponse.From)
            .ToList());
}

public sealed record SupportTicketPage<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

internal static class SupportHttp
{
    public const int MaxPageSize = 100;

    public static IResult ToHttp(this SupportResult result, Func<SupportResult, IResult> onSuccess)
    {
        if (result.IsSuccess) return onSuccess(result);

        var body = new { error = result.Error, detail = result.Detail };
        return result.Error switch
        {
            SupportErrors.TicketNotFound or SupportErrors.TenantNotFound or SupportErrors.AssigneeNotFound
                => Results.NotFound(body),
            SupportErrors.InvalidTicket or SupportErrors.InvalidMessage or SupportErrors.InvalidActor
                => Results.BadRequest(body),
            _ => Results.Conflict(body),
        };
    }

    public static IResult NoContent(SupportResult _) => Results.NoContent();

    public static (int Page, int Size) Paging(int? page, int? pageSize) =>
        (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 25, 1, MaxPageSize));

    /// <summary>A body is required, trimmed, and bounded; null when it is not acceptable.</summary>
    public static string? ValidBody(string? body) =>
        string.IsNullOrWhiteSpace(body) || body.Trim().Length > SupportTicket.BodyMaxLength ? null : body.Trim();

    /// <summary>
    /// The ticket with its thread, tracked. <paramref name="tenantId"/> confines a tenant-side read
    /// to its own tenant; null is the platform.
    /// </summary>
    public static Task<SupportTicket?> LoadAsync(this IQueryable<SupportTicket> tickets, Guid ticketId,
        Guid? tenantId, CancellationToken ct)
    {
        var query = tickets.Include(t => t.Messages).Where(t => t.Id == ticketId);
        if (tenantId is { } id) query = query.Where(t => t.TenantId == id);
        return query.FirstOrDefaultAsync(ct);
    }
}

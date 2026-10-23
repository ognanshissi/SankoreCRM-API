using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Features.Support.TenantSide;
using Sankore.Admin.Infrastructure;
using Sankore.Admin.Infrastructure.Auth;

namespace Sankore.Admin.Features.Support.PlatformSide;

// The platform's side: every tenant's tickets in one queue, internal notes, assignment. Every
// route carries RequirePlatformAdmin — the service key Sankore.Api holds must not answer a
// tenant's ticket in the platform's name.

/// <summary>A ticket across tenants, with the names an operator needs to read the row.</summary>
public sealed record PlatformSupportTicketListItem(SupportTicketSummary Ticket, string TenantName, string? AssignedAdminName);

public sealed record PlatformSupportTicketDetail(SupportTicketDetail Detail, string TenantName, string? AssignedAdminName);

public sealed record SupportAssignee(Guid Id, string FullName, string Email);

/// <summary>
/// What the queue's tabs count. Suffixed: a property named « New » comes out as « _new » in the
/// generated TypeScript client while the JSON still says « new », so the tab would read nothing.
/// </summary>
public sealed record SupportQueueCounts(
    int NewCount, int InProgressCount, int WaitingOnTenantCount, int ResolvedCount, int ClosedCount, int AssignedToMeCount);

// ── Queue ───────────────────────────────────────────────────────────────────────────────────────

public static class ListPlatformSupportTicketsEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/", async ([FromQuery] SupportTicketStatus? status, [FromQuery] bool? openOnly, [FromQuery] Guid? tenantId,
                    [FromQuery] Guid? assignedAdminId, [FromQuery] bool? assignedToMe, [FromQuery] SupportTicketPriority? priority,
                    [FromQuery] string? search, [FromQuery] int? page, [FromQuery] int? pageSize,
                    HttpContext http, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new ListPlatformSupportTicketsQuery(status, openOnly ?? false, tenantId,
                    assignedToMe == true ? AdminClaims.AdminId(http.User) : assignedAdminId, priority, search, page, pageSize), ct)))
            .RequirePlatformAdmin()
            .WithName("ListPlatformSupportTickets")
            .WithSummary("Support tickets across tenants: the most urgent and the oldest activity first")
            .Produces<SupportTicketPage<PlatformSupportTicketListItem>>()
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        group.MapGet("/counts", async (HttpContext http, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new CountPlatformSupportTicketsQuery(AdminClaims.AdminId(http.User)!.Value), ct)))
            .RequirePlatformAdmin()
            .WithName("CountPlatformSupportTickets")
            .WithSummary("How many tickets per status, and how many open ones are assigned to the caller")
            .Produces<SupportQueueCounts>()
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();
    }
}

public sealed record ListPlatformSupportTicketsQuery(
    SupportTicketStatus? Status, bool OpenOnly, Guid? TenantId, Guid? AssignedAdminId, SupportTicketPriority? Priority,
    string? Search, int? Page, int? PageSize) : IRequest<SupportTicketPage<PlatformSupportTicketListItem>>;

internal sealed class ListPlatformSupportTicketsHandler(AdminDbContext db)
    : IRequestHandler<ListPlatformSupportTicketsQuery, SupportTicketPage<PlatformSupportTicketListItem>>
{
    public async Task<SupportTicketPage<PlatformSupportTicketListItem>> Handle(ListPlatformSupportTicketsQuery query,
        CancellationToken ct)
    {
        var rows = db.SupportTickets.AsNoTracking();
        if (query.Status is { } status) rows = rows.Where(t => t.Status == status);
        else if (query.OpenOnly) rows = rows.Where(t => t.Status != SupportTicketStatus.Closed && t.Status != SupportTicketStatus.Resolved);
        if (query.TenantId is { } tenantId) rows = rows.Where(t => t.TenantId == tenantId);
        if (query.AssignedAdminId is { } adminId) rows = rows.Where(t => t.AssignedAdminId == adminId);
        if (query.Priority is { } priority) rows = rows.Where(t => t.Priority == priority);
        rows = SupportSearch.Apply(rows, query.Search);

        var (page, size) = SupportHttp.Paging(query.Page, query.PageSize);
        var total = await rows.CountAsync(ct);

        // A queue (open tickets) is served oldest-waiting first; a history (resolved, closed, one
        // tenant's page) is read newest first. Priority is stored by name, so it is not an SQL
        // order: the screen shows it beside each row.
        var isQueue = query.TenantId is null
                      && (query.OpenOnly || query.Status is SupportTicketStatus.New or SupportTicketStatus.InProgress
                          or SupportTicketStatus.WaitingOnTenant);
        rows = isQueue ? rows.OrderBy(t => t.LastActivityAt) : rows.OrderByDescending(t => t.LastActivityAt);
        var items = await rows.Skip((page - 1) * size).Take(size).ToListAsync(ct);

        var names = await SupportNames.LoadAsync(db, items, ct);
        return new SupportTicketPage<PlatformSupportTicketListItem>(
            items.Select(t => new PlatformSupportTicketListItem(SupportTicketSummary.From(t),
                    names.Tenant(t.TenantId), names.Admin(t.AssignedAdminId)))
                .ToList(),
            total, page, size);
    }
}

public sealed record CountPlatformSupportTicketsQuery(Guid AdminId) : IRequest<SupportQueueCounts>;

internal sealed class CountPlatformSupportTicketsHandler(AdminDbContext db)
    : IRequestHandler<CountPlatformSupportTicketsQuery, SupportQueueCounts>
{
    public async Task<SupportQueueCounts> Handle(CountPlatformSupportTicketsQuery query, CancellationToken ct)
    {
        var byStatus = await db.SupportTickets.AsNoTracking()
            .GroupBy(t => t.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        var mine = await db.SupportTickets.AsNoTracking()
            .CountAsync(t => t.AssignedAdminId == query.AdminId
                             && t.Status != SupportTicketStatus.Closed && t.Status != SupportTicketStatus.Resolved, ct);

        return new SupportQueueCounts(
            byStatus.GetValueOrDefault(SupportTicketStatus.New),
            byStatus.GetValueOrDefault(SupportTicketStatus.InProgress),
            byStatus.GetValueOrDefault(SupportTicketStatus.WaitingOnTenant),
            byStatus.GetValueOrDefault(SupportTicketStatus.Resolved),
            byStatus.GetValueOrDefault(SupportTicketStatus.Closed),
            mine);
    }
}

// ── Detail ──────────────────────────────────────────────────────────────────────────────────────

public static class GetPlatformSupportTicketEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/{ticketId:guid}", async (Guid ticketId, ISender sender, CancellationToken ct) =>
            {
                var detail = await sender.Send(new GetPlatformSupportTicketQuery(ticketId), ct);
                return detail is null
                    ? Results.NotFound(new { error = SupportErrors.TicketNotFound })
                    : Results.Ok(detail);
            })
            .RequirePlatformAdmin()
            .WithName("GetPlatformSupportTicket")
            .WithSummary("A ticket with its whole thread, internal notes included")
            .Produces<PlatformSupportTicketDetail>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();
    }
}

public sealed record GetPlatformSupportTicketQuery(Guid TicketId) : IRequest<PlatformSupportTicketDetail?>;

internal sealed class GetPlatformSupportTicketHandler(AdminDbContext db)
    : IRequestHandler<GetPlatformSupportTicketQuery, PlatformSupportTicketDetail?>
{
    public async Task<PlatformSupportTicketDetail?> Handle(GetPlatformSupportTicketQuery query, CancellationToken ct)
    {
        var ticket = await db.SupportTickets.AsNoTracking().LoadAsync(query.TicketId, null, ct);
        if (ticket is null) return null;

        var names = await SupportNames.LoadAsync(db, [ticket], ct);
        return new PlatformSupportTicketDetail(SupportTicketDetail.From(ticket, includeInternal: true),
            names.Tenant(ticket.TenantId), names.Admin(ticket.AssignedAdminId));
    }
}

// ── Actions ─────────────────────────────────────────────────────────────────────────────────────

/// <param name="NextStatus">For a reply: the status after it, WaitingOnTenant when omitted. Ignored for a note.</param>
public sealed record PlatformSupportReplyRequest(string Body, bool Internal, SupportTicketStatus? NextStatus);

public sealed record PlatformSupportStatusRequest(SupportTicketStatus Status);

public sealed record PlatformSupportAssignRequest(Guid? AdminId);

public sealed record PlatformSupportPriorityRequest(SupportTicketPriority Priority);

public static class PlatformSupportActionEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/{ticketId:guid}/messages", async (Guid ticketId, PlatformSupportReplyRequest body, HttpContext http,
                    ISender sender, CancellationToken ct) =>
                (await sender.Send(new PlatformSupportActionCommand(ticketId, AdminClaims.AdminId(http.User)!.Value,
                    PlatformSupportAction.Reply, body, null, null, null), ct))
                .ToHttp(SupportHttp.NoContent))
            .RequirePlatformAdmin()
            .WithName("ReplyPlatformSupportTicket")
            .WithSummary("Answer the tenant, or add an internal note the tenant never sees")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();

        group.MapPost("/{ticketId:guid}/status", async (Guid ticketId, PlatformSupportStatusRequest body, HttpContext http,
                    ISender sender, CancellationToken ct) =>
                (await sender.Send(new PlatformSupportActionCommand(ticketId, AdminClaims.AdminId(http.User)!.Value,
                    PlatformSupportAction.Status, null, body.Status, null, null), ct))
                .ToHttp(SupportHttp.NoContent))
            .RequirePlatformAdmin()
            .WithName("ChangeSupportTicketStatus")
            .WithSummary("Set a ticket's status; the change appears in the tenant's thread")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();

        group.MapPost("/{ticketId:guid}/assign", async (Guid ticketId, PlatformSupportAssignRequest body, HttpContext http,
                    ISender sender, CancellationToken ct) =>
                (await sender.Send(new PlatformSupportActionCommand(ticketId, AdminClaims.AdminId(http.User)!.Value,
                    PlatformSupportAction.Assign, null, null, body, null), ct))
                .ToHttp(SupportHttp.NoContent))
            .RequirePlatformAdmin()
            .WithName("AssignSupportTicket")
            .WithSummary("Give a ticket to a platform administrator, or to nobody")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        group.MapPost("/{ticketId:guid}/priority", async (Guid ticketId, PlatformSupportPriorityRequest body, HttpContext http,
                    ISender sender, CancellationToken ct) =>
                (await sender.Send(new PlatformSupportActionCommand(ticketId, AdminClaims.AdminId(http.User)!.Value,
                    PlatformSupportAction.Priority, null, null, null, body.Priority), ct))
                .ToHttp(SupportHttp.NoContent))
            .RequirePlatformAdmin()
            .WithName("ChangeSupportTicketPriority")
            .WithSummary("Re-rate a ticket's priority")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();
    }
}

public enum PlatformSupportAction { Reply, Status, Assign, Priority }

public sealed record PlatformSupportActionCommand(
    Guid TicketId,
    Guid AdminId,
    PlatformSupportAction Action,
    PlatformSupportReplyRequest? Reply,
    SupportTicketStatus? Status,
    PlatformSupportAssignRequest? Assign,
    SupportTicketPriority? Priority) : IRequest<SupportResult>;

internal sealed class PlatformSupportActionHandler(AdminDbContext db, ILogger<PlatformSupportActionHandler> logger)
    : IRequestHandler<PlatformSupportActionCommand, SupportResult>
{
    public async Task<SupportResult> Handle(PlatformSupportActionCommand cmd, CancellationToken ct)
    {
        var admin = await db.PlatformAdmins.AsNoTracking().FirstOrDefaultAsync(a => a.Id == cmd.AdminId, ct);
        if (admin is null) return SupportResult.Fail(SupportErrors.InvalidActor);

        string? text = null;
        if (cmd.Action == PlatformSupportAction.Reply)
        {
            text = SupportHttp.ValidBody(cmd.Reply?.Body);
            if (text is null) return SupportResult.Fail(SupportErrors.InvalidMessage);
        }

        if (cmd.Action == PlatformSupportAction.Assign && cmd.Assign?.AdminId is { } assignee
                                                      && !await db.PlatformAdmins.AnyAsync(a => a.Id == assignee && a.IsActive, ct))
            return SupportResult.Fail(SupportErrors.AssigneeNotFound);

        var ticket = await db.SupportTickets.LoadAsync(cmd.TicketId, null, ct);
        if (ticket is null) return SupportResult.Fail(SupportErrors.TicketNotFound);

        var actor = SupportActor.PlatformAdmin(admin.Id, admin.FullName);
        var now = DateTimeOffset.UtcNow;

        switch (cmd.Action)
        {
            case PlatformSupportAction.Reply:
                if (ticket.PlatformReply(actor, text!, cmd.Reply!.Internal, cmd.Reply.NextStatus, now) is null)
                    return SupportResult.Fail(SupportErrors.TicketClosed);

                // Whoever answers first takes the ticket, unless somebody already holds it.
                ticket.AssignTo(ticket.AssignedAdminId ?? admin.Id);
                break;
            case PlatformSupportAction.Status:
                if (cmd.Status is not { } status || !Enum.IsDefined(status) || !ticket.ChangeStatus(actor, status, now))
                    return SupportResult.Fail(SupportErrors.InvalidTransition);
                break;
            case PlatformSupportAction.Assign:
                ticket.AssignTo(cmd.Assign?.AdminId);
                break;
            case PlatformSupportAction.Priority:
                if (cmd.Priority is not { } priority || !Enum.IsDefined(priority))
                    return SupportResult.Fail(SupportErrors.InvalidTicket);
                ticket.ChangePriority(priority);
                break;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Support ticket {Reference} (tenant {TenantId}): platform {Action} by {AdminId}",
            ticket.Reference, ticket.TenantId, cmd.Action, cmd.AdminId);
        return SupportResult.Ok(ticket.Id, ticket.Number);
    }
}

// ── Assignees ───────────────────────────────────────────────────────────────────────────────────

public static class ListSupportAssigneesEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/", async (AdminDbContext db, CancellationToken ct) =>
                Results.Ok(await db.PlatformAdmins.AsNoTracking()
                    .Where(a => a.IsActive)
                    .OrderBy(a => a.FullName)
                    .Select(a => new SupportAssignee(a.Id, a.FullName, a.Email))
                    .ToListAsync(ct)))
            .RequirePlatformAdmin()
            .WithName("ListSupportAssignees")
            .WithSummary("The active platform administrators a ticket can be given to")
            .Produces<List<SupportAssignee>>()
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();
    }
}

internal sealed class SupportNames(IReadOnlyDictionary<Guid, string> tenants, IReadOnlyDictionary<Guid, string> admins)
{
    public string Tenant(Guid id) => tenants.GetValueOrDefault(id, "—");
    public string? Admin(Guid? id) => id is { } value ? admins.GetValueOrDefault(value) : null;

    public static async Task<SupportNames> LoadAsync(AdminDbContext db, IReadOnlyCollection<SupportTicket> tickets,
        CancellationToken ct)
    {
        var tenantIds = tickets.Select(t => t.TenantId).Distinct().ToList();
        var adminIds = tickets.Where(t => t.AssignedAdminId is not null).Select(t => t.AssignedAdminId!.Value).Distinct().ToList();

        var tenants = await db.Tenants.AsNoTracking().Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var admins = await db.PlatformAdmins.AsNoTracking().Where(a => adminIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.FullName, ct);
        return new SupportNames(tenants, admins);
    }
}

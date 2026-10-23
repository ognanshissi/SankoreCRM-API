using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.Support.TenantSide;

// What Sankore.Api calls with the service key on behalf of a signed-in tenant user. The API names
// the user and decides, from its own permissions, whether the user sees only their own tickets
// (RequesterUserId / RequesterOnly) or the whole tenant's. The registry trusts that decision and
// confines every read and write to the tenant in the route.

// ── List ────────────────────────────────────────────────────────────────────────────────────────

public static class ListTenantSupportTicketsEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/", async (Guid tenantId, [FromQuery] Guid? requesterUserId, [FromQuery] SupportTicketStatus? status,
                    [FromQuery] bool? openOnly, [FromQuery] string? search, [FromQuery] int? page, [FromQuery] int? pageSize,
                    ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new ListTenantSupportTicketsQuery(tenantId, requesterUserId, status,
                    openOnly ?? false, search, page, pageSize), ct)))
            .WithName("ListTenantSupportTickets")
            .WithSummary("A tenant's support tickets, most recent activity first")
            .WithDescription("requesterUserId narrows to one user's tickets; openOnly leaves out Closed ones.")
            .Produces<SupportTicketPage<SupportTicketSummary>>()
            .WithOpenApi();
    }
}

public sealed record ListTenantSupportTicketsQuery(
    Guid TenantId, Guid? RequesterUserId, SupportTicketStatus? Status, bool OpenOnly, string? Search,
    int? Page, int? PageSize) : IRequest<SupportTicketPage<SupportTicketSummary>>;

internal sealed class ListTenantSupportTicketsHandler(AdminDbContext db)
    : IRequestHandler<ListTenantSupportTicketsQuery, SupportTicketPage<SupportTicketSummary>>
{
    public async Task<SupportTicketPage<SupportTicketSummary>> Handle(ListTenantSupportTicketsQuery query, CancellationToken ct)
    {
        var rows = db.SupportTickets.AsNoTracking().Where(t => t.TenantId == query.TenantId);
        if (query.RequesterUserId is { } requester) rows = rows.Where(t => t.RequesterUserId == requester);
        if (query.Status is { } status) rows = rows.Where(t => t.Status == status);
        else if (query.OpenOnly) rows = rows.Where(t => t.Status != SupportTicketStatus.Closed);
        rows = SupportSearch.Apply(rows, query.Search);

        var (page, size) = SupportHttp.Paging(query.Page, query.PageSize);
        var total = await rows.CountAsync(ct);
        var items = await rows.OrderByDescending(t => t.LastActivityAt)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);

        return new SupportTicketPage<SupportTicketSummary>(items.Select(SupportTicketSummary.From).ToList(), total, page, size);
    }
}

// ── Open ────────────────────────────────────────────────────────────────────────────────────────

public static class OpenSupportTicketEndpoint
{
    public sealed record Request(
        Guid UserId,
        string Email,
        string FullName,
        string? Role,
        string Subject,
        SupportTicketCategory Category,
        SupportTicketPriority Priority,
        string Body,
        string? PageUrl,
        string? AppVersion,
        string? UserAgent);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/", async (Guid tenantId, Request body, ISender sender, CancellationToken ct) =>
                (await sender.Send(new OpenSupportTicketCommand(tenantId, body), ct))
                .ToHttp(r => Results.Created($"/api/v1/tenants/{tenantId}/support/tickets/{r.TicketId}",
                    new { id = r.TicketId, number = r.Number, reference = SupportTicket.FormatReference(r.Number ?? 0) })))
            .WithName("OpenSupportTicket")
            .WithSummary("Open a support ticket on behalf of a tenant user")
            .Produces(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();
    }
}

public sealed record OpenSupportTicketCommand(Guid TenantId, OpenSupportTicketEndpoint.Request Body) : IRequest<SupportResult>;

internal sealed class OpenSupportTicketHandler(AdminDbContext db, ILogger<OpenSupportTicketHandler> logger)
    : IRequestHandler<OpenSupportTicketCommand, SupportResult>
{
    public async Task<SupportResult> Handle(OpenSupportTicketCommand cmd, CancellationToken ct)
    {
        var b = cmd.Body;
        if (b.UserId == Guid.Empty || string.IsNullOrWhiteSpace(b.Email) || b.Email.Trim().Length > SupportTicket.EmailMaxLength
            || string.IsNullOrWhiteSpace(b.FullName) || b.FullName.Trim().Length > SupportTicket.NameMaxLength)
            return SupportResult.Fail(SupportErrors.InvalidActor);

        if (string.IsNullOrWhiteSpace(b.Subject) || b.Subject.Trim().Length > SupportTicket.SubjectMaxLength
            || !Enum.IsDefined(b.Category) || !Enum.IsDefined(b.Priority))
            return SupportResult.Fail(SupportErrors.InvalidTicket);

        var text = SupportHttp.ValidBody(b.Body);
        if (text is null) return SupportResult.Fail(SupportErrors.InvalidMessage);

        if (!await db.Tenants.AnyAsync(t => t.Id == cmd.TenantId, ct))
            return SupportResult.Fail(SupportErrors.TenantNotFound);

        var ticket = SupportTicket.Open(cmd.TenantId, SupportActor.TenantUser(b.UserId, b.FullName), b.Email, b.Role,
            b.Subject, b.Category, b.Priority, text, b.PageUrl, b.AppVersion, b.UserAgent, DateTimeOffset.UtcNow);
        db.SupportTickets.Add(ticket);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Support ticket {Reference} opened by {UserId} for tenant {TenantId} ({Priority})",
            ticket.Reference, b.UserId, cmd.TenantId, ticket.Priority);
        return SupportResult.Ok(ticket.Id, ticket.Number);
    }
}

// ── Detail ──────────────────────────────────────────────────────────────────────────────────────

public static class GetTenantSupportTicketEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/{ticketId:guid}", async (Guid tenantId, Guid ticketId, [FromQuery] Guid? requesterUserId,
                    ISender sender, CancellationToken ct) =>
            {
                var detail = await sender.Send(new GetTenantSupportTicketQuery(tenantId, ticketId, requesterUserId), ct);
                return detail is null
                    ? Results.NotFound(new { error = SupportErrors.TicketNotFound })
                    : Results.Ok(detail);
            })
            .WithName("GetTenantSupportTicket")
            .WithSummary("A tenant's ticket and its thread, without the platform's internal notes")
            .Produces<SupportTicketDetail>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();
    }
}

public sealed record GetTenantSupportTicketQuery(Guid TenantId, Guid TicketId, Guid? RequesterUserId)
    : IRequest<SupportTicketDetail?>;

internal sealed class GetTenantSupportTicketHandler(AdminDbContext db)
    : IRequestHandler<GetTenantSupportTicketQuery, SupportTicketDetail?>
{
    public async Task<SupportTicketDetail?> Handle(GetTenantSupportTicketQuery query, CancellationToken ct)
    {
        var ticket = await db.SupportTickets.AsNoTracking().LoadAsync(query.TicketId, query.TenantId, ct);
        if (ticket is null) return null;

        // Somebody else's ticket reads as missing: its existence is not the caller's business.
        if (query.RequesterUserId is { } requester && ticket.RequesterUserId != requester) return null;

        return SupportTicketDetail.From(ticket, includeInternal: false);
    }
}

// ── Reply, close, reopen ────────────────────────────────────────────────────────────────────────

/// <param name="RequesterOnly">True when the user may act on their own tickets only.</param>
public sealed record TenantSupportActorRequest(Guid UserId, string FullName, bool RequesterOnly);

public sealed record TenantSupportReplyRequest(Guid UserId, string FullName, bool RequesterOnly, string Body);

public static class TenantSupportActionEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/{ticketId:guid}/messages", async (Guid tenantId, Guid ticketId, TenantSupportReplyRequest body,
                    ISender sender, CancellationToken ct) =>
                (await sender.Send(new TenantSupportActionCommand(tenantId, ticketId,
                    new TenantSupportActorRequest(body.UserId, body.FullName, body.RequesterOnly),
                    TenantSupportAction.Reply, body.Body), ct))
                .ToHttp(SupportHttp.NoContent))
            .WithName("ReplyTenantSupportTicket")
            .WithSummary("The tenant answers on a ticket; a ticket the platform waited on goes back to it")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();

        group.MapPost("/{ticketId:guid}/close", async (Guid tenantId, Guid ticketId, TenantSupportActorRequest body,
                    ISender sender, CancellationToken ct) =>
                (await sender.Send(new TenantSupportActionCommand(tenantId, ticketId, body, TenantSupportAction.Close, null), ct))
                .ToHttp(SupportHttp.NoContent))
            .WithName("CloseTenantSupportTicket")
            .WithSummary("The tenant closes its ticket")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();

        group.MapPost("/{ticketId:guid}/reopen", async (Guid tenantId, Guid ticketId, TenantSupportActorRequest body,
                    ISender sender, CancellationToken ct) =>
                (await sender.Send(new TenantSupportActionCommand(tenantId, ticketId, body, TenantSupportAction.Reopen, null), ct))
                .ToHttp(SupportHttp.NoContent))
            .WithName("ReopenTenantSupportTicket")
            .WithSummary("The tenant reopens a Resolved or Closed ticket")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();
    }
}

public enum TenantSupportAction { Reply, Close, Reopen }

public sealed record TenantSupportActionCommand(
    Guid TenantId, Guid TicketId, TenantSupportActorRequest Actor, TenantSupportAction Action, string? Body)
    : IRequest<SupportResult>;

internal sealed class TenantSupportActionHandler(AdminDbContext db, ILogger<TenantSupportActionHandler> logger)
    : IRequestHandler<TenantSupportActionCommand, SupportResult>
{
    public async Task<SupportResult> Handle(TenantSupportActionCommand cmd, CancellationToken ct)
    {
        if (cmd.Actor.UserId == Guid.Empty || string.IsNullOrWhiteSpace(cmd.Actor.FullName)
            || cmd.Actor.FullName.Trim().Length > SupportTicket.NameMaxLength)
            return SupportResult.Fail(SupportErrors.InvalidActor);

        string? text = null;
        if (cmd.Action == TenantSupportAction.Reply)
        {
            text = SupportHttp.ValidBody(cmd.Body);
            if (text is null) return SupportResult.Fail(SupportErrors.InvalidMessage);
        }

        var ticket = await db.SupportTickets.LoadAsync(cmd.TicketId, cmd.TenantId, ct);
        if (ticket is null || (cmd.Actor.RequesterOnly && ticket.RequesterUserId != cmd.Actor.UserId))
            return SupportResult.Fail(SupportErrors.TicketNotFound);

        var actor = SupportActor.TenantUser(cmd.Actor.UserId, cmd.Actor.FullName);
        var now = DateTimeOffset.UtcNow;
        var moved = cmd.Action switch
        {
            TenantSupportAction.Reply => ticket.TenantReply(actor, text!, now) is not null,
            TenantSupportAction.Close => ticket.CloseByTenant(actor, now),
            _ => ticket.ReopenByTenant(actor, now),
        };
        if (!moved)
            return SupportResult.Fail(cmd.Action == TenantSupportAction.Reply
                ? SupportErrors.TicketClosed
                : SupportErrors.InvalidTransition);

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Support ticket {Reference}: tenant {Action} by {UserId}",
            ticket.Reference, cmd.Action, cmd.Actor.UserId);
        return SupportResult.Ok(ticket.Id, ticket.Number);
    }
}

// ── Activity feed ───────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One thing the platform did on a tenant's ticket that its requester should hear about: a reply
/// or a status change. Never an internal note.
/// </summary>
public sealed record SupportActivityItem(
    Guid MessageId,
    Guid TicketId,
    long Number,
    string Reference,
    string Subject,
    Guid RequesterUserId,
    string RequesterEmail,
    SupportMessageKind Kind,
    SupportAuthorKind AuthorKind,
    string AuthorName,
    string? Body,
    SupportTicketStatus? Status,
    DateTimeOffset CreatedAt);

/// <summary>
/// What Sankore.Api reads every few minutes to mail and ring the requesters: the registry has no
/// mail engine and no route back to the API. Nothing records what was announced — the API's
/// outbox keys on <see cref="SupportActivityItem.MessageId"/>.
/// </summary>
public static class ListTenantSupportActivityEndpoint
{
    public const int MaxItems = 500;

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/", async (Guid tenantId, [FromQuery] DateTimeOffset since, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new ListTenantSupportActivityQuery(tenantId, since), ct)))
            .WithName("ListTenantSupportActivity")
            .WithSummary("The platform's replies and status changes on a tenant's tickets since an instant")
            .Produces<List<SupportActivityItem>>()
            .WithOpenApi();
    }
}

public sealed record ListTenantSupportActivityQuery(Guid TenantId, DateTimeOffset Since) : IRequest<List<SupportActivityItem>>;

internal sealed class ListTenantSupportActivityHandler(AdminDbContext db)
    : IRequestHandler<ListTenantSupportActivityQuery, List<SupportActivityItem>>
{
    public async Task<List<SupportActivityItem>> Handle(ListTenantSupportActivityQuery query, CancellationToken ct)
    {
        var since = query.Since.ToUniversalTime();
        var rows = await (
                from m in db.SupportMessages.AsNoTracking()
                join t in db.SupportTickets.AsNoTracking() on m.TicketId equals t.Id
                where m.TenantId == query.TenantId
                      && m.CreatedAt >= since
                      && m.AuthorKind != SupportAuthorKind.Tenant
                      && m.Kind != SupportMessageKind.InternalNote
                orderby m.CreatedAt
                select new { m, t.Number, t.Subject, t.RequesterUserId, t.RequesterEmail })
            .Take(ListTenantSupportActivityEndpoint.MaxItems)
            .ToListAsync(ct);

        return rows.Select(r => new SupportActivityItem(
                r.m.Id, r.m.TicketId, r.Number, SupportTicket.FormatReference(r.Number), r.Subject,
                r.RequesterUserId, r.RequesterEmail, r.m.Kind, r.m.AuthorKind, r.m.AuthorName, r.m.Body, r.m.Status, r.m.CreatedAt))
            .ToList();
    }
}

internal static class SupportSearch
{
    /// <summary>A reference (« SUP-000123 », « 123 ») or a fragment of the subject.</summary>
    public static IQueryable<SupportTicket> Apply(IQueryable<SupportTicket> rows, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return rows;

        var term = search.Trim();
        var digits = term.StartsWith("SUP-", StringComparison.OrdinalIgnoreCase) ? term[4..] : term;
        if (long.TryParse(digits, out var number)) return rows.Where(t => t.Number == number);

        var pattern = $"%{term.Replace("%", "").Replace("_", "")}%";
        return rows.Where(t => EF.Functions.ILike(t.Subject, pattern));
    }
}

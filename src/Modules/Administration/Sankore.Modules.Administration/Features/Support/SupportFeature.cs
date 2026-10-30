namespace Sankore.Modules.Administration.Features.Support;

using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.Support;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

// ── Area aggregator ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A tenant user's tickets to the platform team. The registry (Sankore.Admin) holds them, because
/// the platform reads every tenant's in one queue; this area relays to it with the service key,
/// always for the caller's own tenant and in the caller's own name. Without
/// <c>support:read:all</c> the caller sees and touches only the tickets they opened.
/// </summary>
internal static class SupportEndpoints
{
    public static IEndpointRouteBuilder MapSupportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("support/tickets").WithTags("Support");

        group.MapGet("", async ([FromQuery] SupportTicketStatus? status, [FromQuery] bool? openOnly,
                    [FromQuery] bool? mine, [FromQuery] string? search, [FromQuery] int? page, [FromQuery] int? pageSize,
                    ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new ListSupportTicketsQuery(status, openOnly ?? false, mine ?? false,
                    search, page ?? 1, pageSize ?? 25), ct);
                return result.IsSuccess ? Results.Ok(result.Value) : SupportTicketErrors.ToProblem(result.Error!);
            })
            .WithName("ListSupportTickets")
            .WithSummary("My tickets to the platform team — or the institution's, with support:read:all")
            .WithDescription("mine=true narrows to the caller's own even with the wide read. 503 "
                + "TENANT_REGISTRY_UNAVAILABLE. Requires support:read.")
            .RequireAuthorization(Permissions.CanReadSupportTickets.Code)
            .Produces<SupportTicketPageDto>()
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("", async (OpenSupportTicketCommand command, HttpContext http, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(command with { UserAgent = http.Request.Headers.UserAgent.ToString() }, ct);
                return result.IsSuccess
                    ? Results.Created($"support/tickets/{result.Value!.Id}", result.Value)
                    : SupportTicketErrors.ToProblem(result.Error!);
            })
            .WithName("OpenSupportTicket")
            .WithSummary("Open a ticket to the platform team")
            .WithDescription("The page the user was on and the application version help the platform reproduce; "
                + "the browser is read from the request. The platform team is mailed. Audited. Requires support:create.")
            .RequireAuthorization(Permissions.CanCreateSupportTicket.Code)
            .Produces<SupportTicketOpenedDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("{ticketId:guid}", async (Guid ticketId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new GetSupportTicketQuery(ticketId), ct);
                return result.IsSuccess ? Results.Ok(result.Value) : SupportTicketErrors.ToProblem(result.Error!);
            })
            .WithName("GetSupportTicket")
            .WithSummary("A ticket and its thread")
            .WithDescription("404 SUPPORT_TICKET_NOT_FOUND for a ticket the caller may not read. Requires support:read.")
            .RequireAuthorization(Permissions.CanReadSupportTickets.Code)
            .Produces<SupportTicketDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{ticketId:guid}/messages", async (Guid ticketId, ReplySupportTicketBody body, ISender sender,
                    CancellationToken ct) =>
            {
                var result = await sender.Send(new ReplySupportTicketCommand(ticketId, body.Body), ct);
                return result.IsSuccess ? Results.NoContent() : SupportTicketErrors.ToProblem(result.Error!);
            })
            .WithName("ReplySupportTicket")
            .WithSummary("Answer on a ticket; one the platform waited on goes back to it")
            .WithDescription("409 SUPPORT_TICKET_CLOSED — reopen it first. Audited. Requires support:create.")
            .RequireAuthorization(Permissions.CanCreateSupportTicket.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{ticketId:guid}/close", async (Guid ticketId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new ChangeSupportTicketStateCommand(ticketId, Reopen: false), ct);
                return result.IsSuccess ? Results.NoContent() : SupportTicketErrors.ToProblem(result.Error!);
            })
            .WithName("CloseSupportTicket")
            .WithSummary("Close a ticket: the problem went away")
            .WithDescription("Audited. Requires support:create.")
            .RequireAuthorization(Permissions.CanCreateSupportTicket.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{ticketId:guid}/reopen", async (Guid ticketId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new ChangeSupportTicketStateCommand(ticketId, Reopen: true), ct);
                return result.IsSuccess ? Results.NoContent() : SupportTicketErrors.ToProblem(result.Error!);
            })
            .WithName("ReopenSupportTicket")
            .WithSummary("Reopen a Resolved or Closed ticket")
            .WithDescription("409 SUPPORT_INVALID_TRANSITION on any other status. Audited. Requires support:create.")
            .RequireAuthorization(Permissions.CanCreateSupportTicket.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }
}

public sealed record ReplySupportTicketBody(string Body);

/// <summary>Who is acting, read from the token and the user table, never from a request.</summary>
internal sealed record SupportCaller(Guid Id, Guid TenantId, string Email, string FullName, bool SeesAll)
{
    /// <summary>The requester filter the registry applies: the caller, unless they see the whole tenant.</summary>
    public Guid? RequesterFilter => SeesAll ? null : Id;

    public static async Task<SupportCaller?> ReadAsync(AdministrationDbContext db, ICurrentUser currentUser,
        CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == currentUser.Id)
            .Select(u => new { u.Email, u.FullName })
            .FirstOrDefaultAsync(ct);
        if (user?.Email is null) return null;

        return new SupportCaller(currentUser.Id, currentUser.TeamTenantId, user.Email,
            string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName,
            RecordVisibility.SeesAll(currentUser, Permissions.CanReadAllSupportTickets));
    }
}

// ── List ────────────────────────────────────────────────────────────────────────────────────────

internal sealed record ListSupportTicketsQuery(
    SupportTicketStatus? Status, bool OpenOnly, bool Mine, string? Search, int Page, int PageSize)
    : IRequest<Result<SupportTicketPageDto>>;

internal sealed class ListSupportTicketsHandler(ISupportRegistry registry, ICurrentUser currentUser)
    : IRequestHandler<ListSupportTicketsQuery, Result<SupportTicketPageDto>>
{
    public async Task<Result<SupportTicketPageDto>> Handle(ListSupportTicketsQuery query, CancellationToken ct)
    {
        var seesAll = RecordVisibility.SeesAll(currentUser, Permissions.CanReadAllSupportTickets);
        Guid? requester = seesAll && !query.Mine ? null : currentUser.Id;

        var answer = await registry.ListAsync(currentUser.TeamTenantId, requester, query.Status, query.OpenOnly,
            query.Search, Math.Max(1, query.Page), Math.Clamp(query.PageSize, 1, 100), ct);
        if (!answer.IsSuccess) return Result.Fail<SupportTicketPageDto>(answer.Error!);

        var page = answer.Value!;
        return Result.Ok(new SupportTicketPageDto(page.Items.Select(t => t.ToDto(currentUser.Id)).ToList(),
            page.Total, page.Page, page.PageSize));
    }
}

// ── Open ────────────────────────────────────────────────────────────────────────────────────────

/// <param name="PageUrl">The screen the user was on (path only), so the platform can reproduce.</param>
/// <param name="AppVersion">The front-end's version.</param>
/// <param name="UserAgent">Server-set from the request header; whatever the body says is replaced.</param>
public sealed record OpenSupportTicketCommand(
    string Subject,
    SupportTicketCategory Category,
    SupportTicketPriority Priority,
    string Body,
    string? PageUrl,
    string? AppVersion,
    string? UserAgent = null)
    : IRequest<Result<SupportTicketOpenedDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "SupportTicket";
    public string? ResourceId => null;
}

internal sealed class OpenSupportTicketValidator : AbstractValidator<OpenSupportTicketCommand>
{
    public OpenSupportTicketValidator()
    {
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(200)
            .WithMessage("Le sujet est obligatoire (200 caractères au plus).");
        RuleFor(x => x.Body).NotEmpty().MaximumLength(10000)
            .WithMessage("Décrivez votre demande (10 000 caractères au plus).");
        RuleFor(x => x.Category).IsInEnum();
        RuleFor(x => x.Priority).IsInEnum();
        RuleFor(x => x.PageUrl).MaximumLength(500);
        RuleFor(x => x.AppVersion).MaximumLength(50);
    }
}

internal sealed class OpenSupportTicketHandler(
    ISupportRegistry registry,
    ICurrentUser currentUser,
    AdministrationDbContext db,
    SupportNotifier notifier)
    : IRequestHandler<OpenSupportTicketCommand, Result<SupportTicketOpenedDto>>
{
    public async Task<Result<SupportTicketOpenedDto>> Handle(OpenSupportTicketCommand cmd, CancellationToken ct)
    {
        var caller = await SupportCaller.ReadAsync(db, currentUser, ct);
        if (caller is null) return Result.Fail<SupportTicketOpenedDto>(SupportTicketErrors.UserNotFound);

        var answer = await registry.OpenAsync(caller.TenantId, new RegistryOpenTicket(
            caller.Id, caller.Email, caller.FullName, string.Join(", ", currentUser.Roles),
            cmd.Subject.Trim(), cmd.Category, cmd.Priority, cmd.Body.Trim(),
            cmd.PageUrl, cmd.AppVersion, cmd.UserAgent), ct);
        if (!answer.IsSuccess) return Result.Fail<SupportTicketOpenedDto>(answer.Error!);

        var opened = answer.Value!;
        await notifier.OpenedAsync(caller.TenantId, opened.Id, opened.Reference, cmd.Subject.Trim(), cmd.Category,
            cmd.Priority, caller, cmd.Body.Trim(), ct);
        return Result.Ok(new SupportTicketOpenedDto(opened.Id, opened.Number, opened.Reference));
    }
}

// ── Detail ──────────────────────────────────────────────────────────────────────────────────────

internal sealed record GetSupportTicketQuery(Guid TicketId) : IRequest<Result<SupportTicketDto>>;

internal sealed class GetSupportTicketHandler(ISupportRegistry registry, ICurrentUser currentUser)
    : IRequestHandler<GetSupportTicketQuery, Result<SupportTicketDto>>
{
    public async Task<Result<SupportTicketDto>> Handle(GetSupportTicketQuery query, CancellationToken ct)
    {
        var requester = RecordVisibility.SeesAll(currentUser, Permissions.CanReadAllSupportTickets)
            ? (Guid?)null
            : currentUser.Id;

        var answer = await registry.GetAsync(currentUser.TeamTenantId, query.TicketId, requester, ct);
        if (!answer.IsSuccess) return Result.Fail<SupportTicketDto>(answer.Error!);
        return answer.Value is null
            ? Result.Fail<SupportTicketDto>(SupportTicketErrors.TicketNotFound)
            : Result.Ok(answer.Value.ToDto(currentUser.Id));
    }
}

// ── Reply ───────────────────────────────────────────────────────────────────────────────────────

public sealed record ReplySupportTicketCommand(Guid TicketId, string Body)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "SupportTicket";
    public string? ResourceId => TicketId.ToString();
}

internal sealed class ReplySupportTicketValidator : AbstractValidator<ReplySupportTicketCommand>
{
    public ReplySupportTicketValidator()
    {
        RuleFor(x => x.Body).NotEmpty().MaximumLength(10000)
            .WithMessage("Le message est vide ou dépasse 10 000 caractères.");
    }
}

internal sealed class ReplySupportTicketHandler(
    ISupportRegistry registry,
    ICurrentUser currentUser,
    AdministrationDbContext db,
    SupportNotifier notifier,
    TimeProvider clock)
    : IRequestHandler<ReplySupportTicketCommand, Result>
{
    public async Task<Result> Handle(ReplySupportTicketCommand cmd, CancellationToken ct)
    {
        var caller = await SupportCaller.ReadAsync(db, currentUser, ct);
        if (caller is null) return Result.Fail(SupportTicketErrors.UserNotFound);

        var answer = await registry.ReplyAsync(caller.TenantId, cmd.TicketId, caller.Id, caller.FullName,
            requesterOnly: !caller.SeesAll, cmd.Body.Trim(), ct);
        if (!answer.IsSuccess) return Result.Fail(answer.Error!);

        // The platform must see the ticket moved: the subject is read back for the e-mail.
        var ticket = await registry.GetAsync(caller.TenantId, cmd.TicketId, caller.RequesterFilter, ct);
        if (ticket.Value is { } detail)
            await notifier.TenantRepliedAsync(caller.TenantId, cmd.TicketId, detail.Ticket.Reference,
                detail.Ticket.Subject, caller.FullName, cmd.Body.Trim(), clock.GetUtcNow(), ct);
        return Result.Ok();
    }
}

// ── Close, reopen ───────────────────────────────────────────────────────────────────────────────

public sealed record ChangeSupportTicketStateCommand(Guid TicketId, bool Reopen)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "SupportTicket";
    public string? ResourceId => TicketId.ToString();
}

internal sealed class ChangeSupportTicketStateHandler(
    ISupportRegistry registry,
    ICurrentUser currentUser,
    AdministrationDbContext db)
    : IRequestHandler<ChangeSupportTicketStateCommand, Result>
{
    public async Task<Result> Handle(ChangeSupportTicketStateCommand cmd, CancellationToken ct)
    {
        var caller = await SupportCaller.ReadAsync(db, currentUser, ct);
        if (caller is null) return Result.Fail(SupportTicketErrors.UserNotFound);

        var answer = cmd.Reopen
            ? await registry.ReopenAsync(caller.TenantId, cmd.TicketId, caller.Id, caller.FullName, !caller.SeesAll, ct)
            : await registry.CloseAsync(caller.TenantId, cmd.TicketId, caller.Id, caller.FullName, !caller.SeesAll, ct);
        return answer.IsSuccess ? Result.Ok() : Result.Fail(answer.Error!);
    }
}

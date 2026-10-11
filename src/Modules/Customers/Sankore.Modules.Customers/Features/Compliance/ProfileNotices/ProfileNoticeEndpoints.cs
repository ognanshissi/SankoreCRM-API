namespace Sankore.Modules.Customers.Features.Compliance.ProfileNotices;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// The CIMA LBC/FT fifteen-day deadline, made measurable: four routes, one clock.
/// </summary>
internal static class ProfileNoticeEndpoints
{
    internal static IEndpointRouteBuilder MapProfileNoticeEndpoints(this IEndpointRouteBuilder app)
    {
        // Before the {clientId:guid} routes of this area would otherwise swallow it: a literal
        // segment and a Guid route parameter do not collide, but keeping the report first makes
        // the intent obvious to the next person adding a route here.
        app.MapGet("profile-change-notices/overdue", Overdue)
            .WithName("ListOverdueProfileNotices")
            .WithSummary("Known profile changes the institution is late integrating")
            .WithDescription(
                "Open notices older than the tenant's profile-change-integration-days (15 by "
                + "default, the CIMA LBC/FT figure), oldest first. A REPORT and not a gate: by the "
                + "time a notice is overdue the breach has happened, and refusing the very update "
                + "that would close it would be perverse. Each row carries the elapsed days and "
                + "the allowance, so a screen does not hard-code fifteen. "
                + "Requires permission: customers:read.")
            // customers:read, matching the settings listing of this same area: a compliance
            // screen that shows how late the institution is must not require the right to change
            // a client file. Closing a notice does — those three routes take customers:update.
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<PagedResult<OverdueProfileNoticeDto>>(StatusCodes.Status200OK)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapPost("{clientId:guid}/profile-change-notices", Record)
            .WithName("RecordProfileChangeNotice")
            .WithSummary("Record that a change to a client's profile has become known")
            .WithDescription(
                "Starts the fifteen-day clock. `noticedOn` is when the INSTITUTION learned of the "
                + "change — an agent records on Monday what a client said on Friday — and a future "
                + "date is refused. The recorder is taken from the token, never from the body. "
                + "`kind` is a closed list so the delays are reportable; `note` is for an "
                + "operational remark and MUST NOT carry the new value, which belongs in the "
                + "client record where it is encrypted. "
                + "Requires permission: customers:update.")
            .RequireAuthorization(Permissions.CanUpdateCustomer.Code)
            .Produces<RecordProfileNoticeResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapPost("profile-change-notices/{noticeId:guid}/integration", Integrate)
            .WithName("IntegrateProfileChangeNotice")
            .WithSummary("Close a notice: the change has been carried into the record")
            .WithDescription(
                "Stops the clock. Refuses an already-closed notice, and refuses an integration "
                + "dated before the change was noticed — which would show as a negative delay in "
                + "the compliance report. Requires permission: customers:update.")
            .RequireAuthorization(Permissions.CanUpdateCustomer.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapPost("profile-change-notices/{noticeId:guid}/dismissal", Dismiss)
            .WithName("DismissProfileChangeNotice")
            .WithSummary("Close a notice that turned out to need no update")
            .WithDescription(
                "For a client who was mistaken, or a change that was about somebody else. The "
                + "reason is REQUIRED: a notice that simply disappears is indistinguishable from "
                + "one somebody forgot. A dismissed notice is never overdue. "
                + "Requires permission: customers:update.")
            .RequireAuthorization(Permissions.CanUpdateCustomer.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Overdue(
        int? page, int? pageSize, ISender sender, CancellationToken ct)
        => Results.Ok((await sender.Send(
            new OverdueProfileNoticesQuery(page ?? 1, pageSize ?? 50), ct)).Value);

    private static async Task<IResult> Record(
        Guid clientId, RecordProfileNoticeRequest body, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new RecordProfileNoticeCommand(clientId, body.Kind, body.NoticedOn, body.Note), ct);

        if (result.IsSuccess)
        {
            return Results.Created(
                $"/api/v1/clients/profile-change-notices/{result.Value}",
                new RecordProfileNoticeResponse(result.Value));
        }

        return result.Error == CustomerErrors.ClientNotFound
            ? Results.NotFound(new { error = result.Error })
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> Integrate(
        Guid noticeId, ISender sender, CancellationToken ct)
        => Respond(await sender.Send(new IntegrateProfileNoticeCommand(noticeId), ct));

    private static async Task<IResult> Dismiss(
        Guid noticeId, DismissProfileNoticeRequest body, ISender sender, CancellationToken ct)
        => Respond(await sender.Send(
            new DismissProfileNoticeCommand(noticeId, body.Reason), ct));

    private static IResult Respond(Result result)
    {
        if (result.IsSuccess) return Results.NoContent();

        return result.Error switch
        {
            CustomerErrors.ProfileNoticeNotFound => Results.NotFound(new { error = result.Error }),
            CustomerErrors.ProfileNoticeAlreadyClosed => Results.Conflict(new { error = result.Error }),
            _ => Results.BadRequest(new { error = result.Error }),
        };
    }
}

public sealed record RecordProfileNoticeResponse(Guid NoticeId);

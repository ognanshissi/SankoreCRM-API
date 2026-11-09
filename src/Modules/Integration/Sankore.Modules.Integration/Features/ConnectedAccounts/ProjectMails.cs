namespace Sankore.Modules.Integration.Features.ConnectedAccounts;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// The e-mails filed on a project (project documents lot 1, 2026-11-08): sent from its page —
/// with its devis, its proposal — and the replies in their conversations. Same reading as a
/// complaint's: a colleague sees when, which way and from whose mailbox; the subject and preview
/// only reach the mailbox's owner, who opens the conversation from there.
/// </summary>
/// <param name="MessageId">Set for the caller's own messages only — what opens the conversation.</param>
public sealed record ProjectMailDto(
    Guid? MessageId,
    DateTimeOffset SentAt,
    string Direction,
    Guid UserId,
    string? UserName,
    bool Mine,
    string? Subject,
    string? Snippet,
    bool HasAttachments);

internal sealed record ListProjectMailsQuery(Guid ProjectId) : IRequest<Result<IReadOnlyList<ProjectMailDto>>>;

internal sealed class ListProjectMailsHandler(
    IntegrationDbContext db,
    ICurrentUser me,
    CalendarEventProtector protector,
    ILeadsModule leads,
    IAdministrationModule admin)
    : IRequestHandler<ListProjectMailsQuery, Result<IReadOnlyList<ProjectMailDto>>>
{
    public const string NotFound = "OPPORTUNITY_NOT_FOUND";

    public async Task<Result<IReadOnlyList<ProjectMailDto>>> Handle(ListProjectMailsQuery query, CancellationToken ct)
    {
        // The project page's perimeter, asked of M13: outside it the project does not exist.
        if (await leads.GetProjectForMailAsync(me.TenantId, query.ProjectId, me.Id, ct) is null)
            return Result.Fail<IReadOnlyList<ProjectMailDto>>(NotFound);

        var rows = await (
                from link in db.ConnectedMailLinks
                join message in db.ConnectedMailMessages on link.MessageId equals message.Id
                where link.EntityType == MailLinkEntity.Project && link.EntityId == query.ProjectId
                orderby link.SentAt descending
                select new { link.MessageId, link.SentAt, link.Direction, link.UserId, message.HasAttachments })
            .Take(200)
            .ToListAsync(ct);

        var mineIds = rows.Where(r => r.UserId == me.Id).Select(r => r.MessageId).ToList();
        var mine = mineIds.Count == 0
            ? new Dictionary<Guid, ConnectedMailMessage>()
            : await db.ConnectedMailMessages.Where(m => mineIds.Contains(m.Id) && m.UserId == me.Id).ToDictionaryAsync(m => m.Id, ct);

        var userIds = rows.Select(r => r.UserId).Distinct().ToList();
        var names = userIds.Count == 0 ? new Dictionary<Guid, string>() : await admin.GetUserNamesAsync(me.TenantId, userIds, ct);

        return Result.Ok<IReadOnlyList<ProjectMailDto>>(rows.Select(r =>
        {
            var own = mine.GetValueOrDefault(r.MessageId);
            return new ProjectMailDto(
                own?.Id,
                r.SentAt,
                r.Direction.ToString(),
                r.UserId,
                names.GetValueOrDefault(r.UserId),
                own is not null,
                own is null ? null : protector.Reveal(own.SubjectEnc),
                own is null ? null : protector.Reveal(own.SnippetEnc),
                r.HasAttachments);
        }).ToList());
    }
}

internal static class ProjectMailsEndpoints
{
    internal static IEndpointRouteBuilder MapProjectMailsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("integration/projects/{projectId:guid}/mails",
                async (Guid projectId, ISender sender, CancellationToken ct) =>
                {
                    var result = await sender.Send(new ListProjectMailsQuery(projectId), ct);
                    return result.IsSuccess
                        ? Results.Ok(result.Value)
                        : Results.Problem(title: "Messagerie", detail: result.Error, statusCode: StatusCodes.Status404NotFound);
                })
            .WithName("ListProjectMails")
            .WithTags("ConnectedMailbox")
            .WithSummary("The e-mails filed on a project, newest first — subject and preview for the mailbox's owner only")
            .WithDescription(
                "Filed by the mailbox sync: a message sent with `projectId` (recognised by the id its provider gave it) and "
                + "the replies in its conversation. 404 OPPORTUNITY_NOT_FOUND outside the project page's perimeter. "
                + "Requires lead:opportunity:read.")
            .RequireAuthorization(Permissions.CanReadOpportunities.Code)
            .Produces<IReadOnlyList<ProjectMailDto>>()
            .Produces(StatusCodes.Status404NotFound)
            .WithTenantHeader();
        return app;
    }
}

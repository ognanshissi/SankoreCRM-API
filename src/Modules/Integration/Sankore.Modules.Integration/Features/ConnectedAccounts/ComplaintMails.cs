namespace Sankore.Modules.Integration.Features.ConnectedAccounts;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// The e-mails of a client complaint (2026-10-31, complaints lot 2): those filed on it by the sync
/// — sent from it, or the client's reply quoting its reference. A colleague sees when, which way and
/// from whose mailbox; the subject and preview only reach the mailbox's owner, who opens the
/// conversation from there — the connected mailbox's rule, unchanged (product owner).
/// </summary>
/// <param name="MessageId">Set for the caller's own messages only — what opens the conversation.</param>
public sealed record ComplaintMailDto(
    Guid? MessageId,
    DateTimeOffset SentAt,
    string Direction,
    Guid UserId,
    string? UserName,
    bool Mine,
    string? Subject,
    string? Snippet);

internal sealed record ListComplaintMailsQuery(Guid ClientId, Guid ComplaintId) : IRequest<Result<IReadOnlyList<ComplaintMailDto>>>;

internal sealed class ListComplaintMailsHandler(
    IntegrationDbContext db,
    ICurrentUser me,
    CalendarEventProtector protector,
    ICustomersModule customers,
    IAdministrationModule admin)
    : IRequestHandler<ListComplaintMailsQuery, Result<IReadOnlyList<ComplaintMailDto>>>
{
    public const string NotFound = "COMPLAINT_NOT_FOUND";

    public async Task<Result<IReadOnlyList<ComplaintMailDto>>> Handle(ListComplaintMailsQuery query, CancellationToken ct)
    {
        // The client's perimeter, then the complaint is that client's — folded into one « not found ».
        if (!await customers.CanActorAccessClientAsync(me.TenantId, query.ClientId, me.Id, ct)
            || await customers.GetComplaintReferenceAsync(me.TenantId, query.ClientId, query.ComplaintId, ct) is null)
            return Result.Fail<IReadOnlyList<ComplaintMailDto>>(NotFound);

        var links = await db.ConnectedMailLinks
            .Where(l => l.EntityType == MailLinkEntity.Complaint && l.EntityId == query.ComplaintId)
            .OrderByDescending(l => l.SentAt)
            .Take(200)
            .ToListAsync(ct);

        var mineIds = links.Where(l => l.UserId == me.Id).Select(l => l.MessageId).ToList();
        var mine = mineIds.Count == 0
            ? new Dictionary<Guid, ConnectedMailMessage>()
            : await db.ConnectedMailMessages.Where(m => mineIds.Contains(m.Id) && m.UserId == me.Id).ToDictionaryAsync(m => m.Id, ct);

        var userIds = links.Select(l => l.UserId).Distinct().ToList();
        var names = userIds.Count == 0 ? new Dictionary<Guid, string>() : await admin.GetUserNamesAsync(me.TenantId, userIds, ct);

        return Result.Ok<IReadOnlyList<ComplaintMailDto>>(links.Select(l =>
        {
            var own = mine.GetValueOrDefault(l.MessageId);
            return new ComplaintMailDto(
                own?.Id,
                l.SentAt,
                l.Direction.ToString(),
                l.UserId,
                names.GetValueOrDefault(l.UserId),
                own is not null,
                own is null ? null : protector.Reveal(own.SubjectEnc),
                own is null ? null : protector.Reveal(own.SnippetEnc));
        }).ToList());
    }
}

internal static class ComplaintMailsEndpoints
{
    internal static IEndpointRouteBuilder MapComplaintMailsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("integration/clients/{clientId:guid}/complaints/{complaintId:guid}/mails",
                async (Guid clientId, Guid complaintId, ISender sender, CancellationToken ct) =>
                {
                    var result = await sender.Send(new ListComplaintMailsQuery(clientId, complaintId), ct);
                    return result.IsSuccess
                        ? Results.Ok(result.Value)
                        : Results.Problem(title: "Messagerie", detail: result.Error, statusCode: StatusCodes.Status404NotFound);
                })
            .WithName("ListComplaintMails")
            .WithTags("ConnectedMailbox")
            .WithSummary("The e-mails filed on a client complaint, newest first — subject and preview for the mailbox's owner only")
            .WithDescription(
                "Filed by the mailbox sync when the subject quotes the complaint's reference (RCL-yyyy-nnnnnn) and the "
                + "complaint's client is a party. 404 COMPLAINT_NOT_FOUND outside the client's perimeter or for another "
                + "client's complaint. Requires customers:complaints:read.")
            .RequireAuthorization(Permissions.CanReadClientComplaints.Code)
            .Produces<IReadOnlyList<ComplaintMailDto>>()
            .Produces(StatusCodes.Status404NotFound)
            .WithTenantHeader();
        return app;
    }
}

namespace Sankore.Modules.Administration.Features.Support;

using Microsoft.AspNetCore.Http;
using Sankore.Modules.Administration.Infrastructure.PaidModules;
using Sankore.Modules.Administration.Infrastructure.Support;

// The names of these enums are the registry's (Sankore.Admin, Domain/SupportTicket.cs): its
// answers deserialize straight into them. Rename one on both sides or not at all.

public enum SupportTicketStatus { New, InProgress, WaitingOnTenant, Resolved, Closed }

public enum SupportTicketCategory { Bug, Question, Access, ModulesBilling, Suggestion }

public enum SupportTicketPriority { Low, Normal, High, Blocking }

public enum SupportAuthorKind { Tenant, Platform, System }

public enum SupportMessageKind { Reply, InternalNote, StatusChange }

/// <param name="IsMine">The caller opened it.</param>
public sealed record SupportTicketSummaryDto(
    Guid Id,
    long Number,
    string Reference,
    string Subject,
    SupportTicketCategory Category,
    SupportTicketPriority Priority,
    SupportTicketStatus Status,
    Guid RequesterUserId,
    string RequesterName,
    bool IsMine,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt);

/// <summary>
/// One entry of the thread. Who answered at the platform is a name, never an id: the tenant reads
/// the answer, not the platform's directory.
/// </summary>
/// <param name="IsMine">Written by the caller — the thread aligns it on their side.</param>
public sealed record SupportMessageDto(
    Guid Id,
    SupportMessageKind Kind,
    SupportAuthorKind AuthorKind,
    string AuthorName,
    string? Body,
    SupportTicketStatus? Status,
    bool IsMine,
    DateTimeOffset CreatedAt);

/// <summary>A ticket and its thread, with what the caller may do next — the screen derives nothing.</summary>
public sealed record SupportTicketDto(
    SupportTicketSummaryDto Ticket,
    string? PageUrl,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<SupportMessageDto> Messages,
    bool CanReply,
    bool CanClose,
    bool CanReopen);

public sealed record SupportTicketPageDto(IReadOnlyList<SupportTicketSummaryDto> Items, int Total, int Page, int PageSize);

public sealed record SupportTicketOpenedDto(Guid Id, long Number, string Reference);

/// <summary>
/// Error codes of this area. The registry's codes are relayed as they are; these are the ones
/// both sides use.
/// </summary>
public static class SupportTicketErrors
{
    public const string TicketNotFound = "SUPPORT_TICKET_NOT_FOUND";
    public const string TicketClosed = "SUPPORT_TICKET_CLOSED";
    public const string InvalidTransition = "SUPPORT_INVALID_TRANSITION";
    public const string UserNotFound = "USER_NOT_FOUND";
    public const string RegistryUnavailable = HttpTenantModuleRegistry.Unavailable;

    private static readonly HashSet<string> NotFound = [TicketNotFound, "TENANT_NOT_FOUND", UserNotFound];
    private static readonly HashSet<string> BadRequest = ["INVALID_ACTOR", "INVALID_SUPPORT_TICKET", "INVALID_SUPPORT_MESSAGE"];

    internal static IResult ToProblem(string error, string? detail = null)
    {
        var body = new { error, detail };
        if (error == RegistryUnavailable) return Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        if (NotFound.Contains(error)) return Results.NotFound(body);
        if (BadRequest.Contains(error)) return Results.BadRequest(body);
        return Results.Conflict(body);
    }

    internal static SupportTicketSummaryDto ToDto(this RegistrySupportTicket t, Guid me) => new(
        t.Id, t.Number, t.Reference, t.Subject, t.Category, t.Priority, t.Status,
        t.RequesterUserId, t.RequesterName, t.RequesterUserId == me, t.CreatedAt, t.LastActivityAt);

    internal static SupportTicketDto ToDto(this RegistrySupportTicketDetail d, Guid me)
    {
        var status = d.Ticket.Status;
        return new SupportTicketDto(
            d.Ticket.ToDto(me),
            d.PageUrl,
            d.ResolvedAt,
            d.ClosedAt,
            d.Messages
                // The registry never sends one on this route; a second filter costs nothing.
                .Where(m => m.Kind != SupportMessageKind.InternalNote)
                .Select(m => new SupportMessageDto(m.Id, m.Kind, m.AuthorKind, m.AuthorName, m.Body, m.Status,
                    m.AuthorKind == SupportAuthorKind.Tenant && m.AuthorId == me, m.CreatedAt))
                .ToList(),
            CanReply: status != SupportTicketStatus.Closed,
            CanClose: status != SupportTicketStatus.Closed,
            CanReopen: status is SupportTicketStatus.Resolved or SupportTicketStatus.Closed);
    }
}

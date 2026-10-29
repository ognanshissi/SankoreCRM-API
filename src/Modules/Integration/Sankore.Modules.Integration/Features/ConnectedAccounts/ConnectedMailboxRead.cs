namespace Sankore.Modules.Integration.Features.ConnectedAccounts;

using Hangfire;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

// Connected mailbox, lot 2: the caller reads THEIR OWN kept e-mails. Nobody else's — a colleague
// will only ever see that an exchange happened (the lead/client tab, lot 6).

// ── DTOs ─────────────────────────────────────────────────────────────────────────────────────

public sealed record MailPartyDto(string Address, string? Name);

/// <param name="Id">The provider's attachment id, for the download (lot 5).</param>
public sealed record MailAttachmentDto(string Id, string Name, string? ContentType, long Size, bool IsInline);

/// <param name="EntityType"><c>Lead</c> or <c>Client</c>.</param>
/// <param name="Name">The lead's or client's name, so the screen never shows an identifier; null when the record is gone.</param>
public sealed record MailLinkDto(string EntityType, Guid EntityId, string? Name = null);

/// <summary>One conversation of the caller's mailboxes, as its latest message shows it.</summary>
/// <param name="LatestMessageId">Opens the conversation: <c>GET me/mail/messages/{id}/thread</c>.</param>
/// <param name="LastDirection"><c>Received</c> or <c>Sent</c>.</param>
/// <param name="Links">Every lead and client the conversation was exchanged with.</param>
public sealed record MailThreadSummaryDto(
    Guid LatestMessageId,
    Guid AccountId,
    string AccountLabel,
    string? Subject,
    string? Snippet,
    MailPartyDto? From,
    IReadOnlyList<MailPartyDto> To,
    DateTimeOffset LastAt,
    string LastDirection,
    int MessageCount,
    int UnreadCount,
    bool HasAttachments,
    IReadOnlyList<MailLinkDto> Links);

/// <param name="Body">HTML when <c>BodyIsHtml</c>: UNSANITISED, as the sender wrote it — render it sandboxed.</param>
/// <param name="BodyTruncated">The body was longer than 500 000 characters and was cut.</param>
public sealed record MailMessageDto(
    Guid Id,
    Guid AccountId,
    string Direction,
    string Folder,
    DateTimeOffset SentAt,
    bool IsRead,
    MailPartyDto? From,
    IReadOnlyList<MailPartyDto> To,
    IReadOnlyList<MailPartyDto> Cc,
    string? Subject,
    string? Body,
    bool BodyIsHtml,
    bool BodyTruncated,
    IReadOnlyList<MailAttachmentDto> Attachments,
    IReadOnlyList<MailLinkDto> Links);

/// <summary>The four tabs of the Messagerie: every conversation, unread ones, with a lead, with a client.</summary>
public sealed record MailThreadCountsDto(int All, int Unread, int Leads, int Clients);

/// <summary>A page of conversations, plus the tab counts under the same mailbox and search.</summary>
public sealed record MailThreadPageDto(IReadOnlyList<MailThreadSummaryDto> Items, int TotalCount, int Page, int PageSize, MailThreadCountsDto Counts);

public sealed record MailThreadDto(Guid AccountId, string AccountLabel, string? Subject, IReadOnlyList<MailMessageDto> Messages);

// ── Threads ──────────────────────────────────────────────────────────────────────────────────

/// <param name="Page">1-based.</param>
/// <param name="Entity">« Lead » or « Client »: only conversations with one; null for all.</param>
/// <param name="Search">Words looked for in the lead's or client's name, the subject and the addresses.</param>
internal sealed record ListMyMailThreadsQuery(
    Guid? AccountId, bool UnreadOnly, int Page, int PageSize, string? Entity = null, string? Search = null)
    : IRequest<MailThreadPageDto>;

/// <summary>
/// The caller's conversations, latest first. Grouped in SQL on (account, thread) — counts and the
/// last date — then only the page's latest messages are read and decrypted.
///
/// <para>
/// <b>The search decrypts, and only the caller's own messages.</b> Subjects and addresses are
/// ciphertext, so no SQL predicate can match them: the newest <see cref="SearchScanLimit"/>
/// messages of the caller's mailboxes are decrypted here, matched with the names of their leads
/// and clients, and the matching conversations are what the page is built from. A colleague's
/// message is never read — the scan starts from <c>UserId == me.Id</c> like everything else here.
/// </para>
/// </summary>
internal sealed class ListMyMailThreadsHandler(IntegrationDbContext db, ICurrentUser me, ConnectedMailboxSync sync, MailLinkNames? names = null)
    : IRequestHandler<ListMyMailThreadsQuery, MailThreadPageDto>
{
    /// <summary>Messages decrypted for one search: a mailbox only keeps exchanges with leads and clients.</summary>
    internal const int SearchScanLimit = 5000;

    public async Task<MailThreadPageDto> Handle(ListMyMailThreadsQuery query, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 100);

        var messages = db.ConnectedMailMessages.Where(m => m.UserId == me.Id);
        if (query.AccountId is { } accountId) messages = messages.Where(m => m.AccountId == accountId);

        if (Normalize(query.Search) is { Length: > 0 } search)
        {
            var keys = await MatchingThreadsAsync(messages, search, ct);
            if (keys.Count == 0) return new MailThreadPageDto([], 0, page, size, new MailThreadCountsDto(0, 0, 0, 0));
            messages = messages.Where(m => keys.Contains(m.ThreadKey));
        }

        var counts = new MailThreadCountsDto(
            await Group(messages).CountAsync(ct),
            await Group(messages).Where(g => g.Unread > 0).CountAsync(ct),
            await Group(WithLink(db, messages, MailLinkEntity.Lead)).CountAsync(ct),
            await Group(WithLink(db, messages, MailLinkEntity.Client)).CountAsync(ct));

        if (Enum.TryParse<MailLinkEntity>(query.Entity, ignoreCase: true, out var entity)) messages = WithLink(db, messages, entity);

        var grouped = Group(messages);
        if (query.UnreadOnly) grouped = grouped.Where(g => g.Unread > 0);

        var total = await grouped.CountAsync(ct);
        var threads = await grouped
            .OrderByDescending(g => g.LastAt).ThenBy(g => g.ThreadKey)
            .Skip((page - 1) * size).Take(size)
            .ToListAsync(ct);

        if (threads.Count == 0) return new MailThreadPageDto([], total, page, size, counts);

        var accountIds = threads.Select(t => t.AccountId).Distinct().ToList();
        var threadKeys = threads.Select(t => t.ThreadKey).Distinct().ToList();

        var rows = await messages
            .Where(m => accountIds.Contains(m.AccountId) && threadKeys.Contains(m.ThreadKey))
            .ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();
        var links = await db.ConnectedMailLinks.Where(l => ids.Contains(l.MessageId)).ToListAsync(ct);
        var labels = await db.ConnectedAccounts.Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Label, ct);
        var linkNames = names is null ? null : await names.ResolveAsync(links, ct);

        var items = threads.Select(t =>
        {
            var inThread = rows.Where(r => r.AccountId == t.AccountId && r.ThreadKey == t.ThreadKey).ToList();
            var latest = inThread.OrderByDescending(r => r.SentAt).ThenByDescending(r => r.CreatedAt).First();
            var threadIds = inThread.Select(r => r.Id).ToHashSet();

            return new MailThreadSummaryDto(
                latest.Id, t.AccountId, labels.GetValueOrDefault(t.AccountId) ?? string.Empty,
                sync.RevealText(latest.SubjectEnc),
                sync.RevealText(latest.SnippetEnc),
                Party(sync.Reveal<MailParty>(latest.FromEnc)),
                Parties(sync.Reveal<List<MailParty>>(latest.ToEnc)),
                t.LastAt, latest.Direction.ToString(), t.Count, t.Unread, t.Attachments,
                MailLinks.Of(links.Where(l => threadIds.Contains(l.MessageId)), linkNames));
        }).ToList();

        return new MailThreadPageDto(items, total, page, size, counts);
    }

    // Member initialisation, not a positional record: EF composes a Where or an OrderBy on a
    // member-init projection, never on one built through a constructor.
    internal sealed class ThreadGroup
    {
        public Guid AccountId { get; init; }
        public string ThreadKey { get; init; } = string.Empty;
        public DateTimeOffset LastAt { get; init; }
        public int Count { get; init; }
        public int Unread { get; init; }
        public bool Attachments { get; init; }
    }

    internal static IQueryable<ThreadGroup> Group(IQueryable<ConnectedMailMessage> messages) => messages
        .GroupBy(m => new { m.AccountId, m.ThreadKey })
        .Select(g => new ThreadGroup
        {
            AccountId = g.Key.AccountId,
            ThreadKey = g.Key.ThreadKey,
            LastAt = g.Max(m => m.SentAt),
            Count = g.Count(),
            Unread = g.Count(m => !m.IsRead),
            Attachments = g.Any(m => m.HasAttachments),
        });

    /// <summary>Messages linked to a lead (or a client): every kept message has at least one link.</summary>
    internal static IQueryable<ConnectedMailMessage> WithLink(IntegrationDbContext db, IQueryable<ConnectedMailMessage> messages, MailLinkEntity entity) =>
        messages.Where(m => db.ConnectedMailLinks.Any(l => l.MessageId == m.Id && l.EntityType == entity));

    /// <summary>The conversations whose subject, an address, or the name of their lead or client holds every word searched.</summary>
    private async Task<List<string>> MatchingThreadsAsync(IQueryable<ConnectedMailMessage> messages, string search, CancellationToken ct)
    {
        var scanned = await messages
            .OrderByDescending(m => m.SentAt)
            .Take(SearchScanLimit)
            .Select(m => new { m.Id, m.ThreadKey, m.SubjectEnc, m.FromEnc, m.ToEnc, m.CcEnc })
            .ToListAsync(ct);
        if (scanned.Count == 0) return [];

        var ids = scanned.Select(m => m.Id).ToList();
        var links = await db.ConnectedMailLinks.Where(l => ids.Contains(l.MessageId)).ToListAsync(ct);
        var linkNames = names is null ? new Dictionary<(MailLinkEntity, Guid), string>() : await names.ResolveAsync(links, ct);
        var namesByMessage = links.ToLookup(l => l.MessageId, l => linkNames.GetValueOrDefault((l.EntityType, l.EntityId)));

        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var m in scanned)
        {
            if (keys.Contains(m.ThreadKey)) continue;

            var parties = new[] { sync.Reveal<MailParty>(m.FromEnc) }
                .Concat(sync.Reveal<List<MailParty>>(m.ToEnc) ?? [])
                .Concat(sync.Reveal<List<MailParty>>(m.CcEnc) ?? [])
                .Where(p => p is not null)
                .SelectMany(p => new[] { p!.Address, p.Name });

            var haystack = Normalize(string.Join(' ',
                new[] { sync.RevealText(m.SubjectEnc) }.Concat(parties).Concat(namesByMessage[m.Id]).Where(v => !string.IsNullOrWhiteSpace(v))));

            if (haystack is not null && words.All(haystack.Contains)) keys.Add(m.ThreadKey);
        }

        return keys.ToList();
    }

    /// <summary>Lower-case, accents removed, spaces collapsed: « Koné » is found by « kone ».</summary>
    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var decomposed = value.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var kept = decomposed.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark);
        return System.Text.RegularExpressions.Regex.Replace(new string(kept.ToArray()), @"\s+", " ");
    }

    internal static MailPartyDto? Party(MailParty? p) => p is null ? null : new MailPartyDto(p.Address, p.Name);

    internal static IReadOnlyList<MailPartyDto> Parties(IEnumerable<MailParty>? list) =>
        list?.Select(p => new MailPartyDto(p.Address, p.Name)).ToList() ?? [];
}

internal static class MailLinks
{
    public static IReadOnlyList<MailLinkDto> Of(IEnumerable<ConnectedMailLink> links, IReadOnlyDictionary<(MailLinkEntity, Guid), string>? names = null) =>
        links.Select(l => (l.EntityType, l.EntityId)).Distinct()
            .OrderBy(l => l.EntityType).ThenBy(l => l.EntityId)
            .Select(l => new MailLinkDto(l.EntityType.ToString(), l.EntityId, names?.GetValueOrDefault(l)))
            .ToList();
}

/// <summary>
/// The names behind links (lot 5): clients and leads in one batch read each — a page names a
/// handful, a search the whole mailbox. A module that cannot answer leaves the name null; the
/// screen says « Lead » or « Client » rather than failing the mailbox.
/// </summary>
internal sealed class MailLinkNames(ICurrentUser me, ILeadsModule? leads = null, ICustomersModule? customers = null)
{
    public async Task<IReadOnlyDictionary<(MailLinkEntity, Guid), string>> ResolveAsync(IEnumerable<ConnectedMailLink> links, CancellationToken ct)
    {
        var names = new Dictionary<(MailLinkEntity, Guid), string>();
        var distinct = links.Select(l => (l.EntityType, l.EntityId)).Distinct().ToList();

        try
        {
            var clientIds = distinct.Where(l => l.EntityType == MailLinkEntity.Client).Select(l => l.EntityId).ToList();
            if (customers is not null && clientIds.Count > 0)
            {
                foreach (var (id, summary) in await customers.GetClientSummariesAsync(me.TenantId, clientIds, ct))
                    names[(MailLinkEntity.Client, id)] = summary.DisplayName;
            }

            var leadIds = distinct.Where(l => l.EntityType == MailLinkEntity.Lead).Select(l => l.EntityId).ToList();
            if (leads is not null && leadIds.Count > 0)
            {
                foreach (var (id, name) in await leads.GetLeadNamesAsync(leadIds, ct))
                    names[(MailLinkEntity.Lead, id)] = name;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Names are a comfort: the conversations are shown without them.
        }

        return names;
    }
}

// ── One conversation ─────────────────────────────────────────────────────────────────────────

internal sealed record GetMyMailThreadQuery(Guid MessageId) : IRequest<Result<MailThreadDto>>;

/// <summary>Every kept message of the conversation one message belongs to, oldest first, decrypted — for its owner only.</summary>
internal sealed class GetMyMailThreadHandler(IntegrationDbContext db, ICurrentUser me, ConnectedMailboxSync sync, MailLinkNames? names = null)
    : IRequestHandler<GetMyMailThreadQuery, Result<MailThreadDto>>
{
    public const string NotFound = "MAIL_MESSAGE_NOT_FOUND";

    public async Task<Result<MailThreadDto>> Handle(GetMyMailThreadQuery query, CancellationToken ct)
    {
        // Somebody else's message reads exactly like a message that does not exist.
        var anchor = await db.ConnectedMailMessages.FirstOrDefaultAsync(m => m.Id == query.MessageId && m.UserId == me.Id, ct);
        if (anchor is null) return Result.Fail<MailThreadDto>(NotFound);

        var messages = await db.ConnectedMailMessages
            .Where(m => m.AccountId == anchor.AccountId && m.ThreadKey == anchor.ThreadKey && m.UserId == me.Id)
            .OrderBy(m => m.SentAt).ThenBy(m => m.CreatedAt)
            .ToListAsync(ct);

        var ids = messages.Select(m => m.Id).ToList();
        var linkRows = await db.ConnectedMailLinks.Where(l => ids.Contains(l.MessageId)).ToListAsync(ct);
        var linkNames = names is null ? null : await names.ResolveAsync(linkRows, ct);
        var links = linkRows.ToLookup(l => l.MessageId);
        var label = await db.ConnectedAccounts.Where(a => a.Id == anchor.AccountId).Select(a => a.Label).FirstOrDefaultAsync(ct);

        var dtos = messages.Select(m => new MailMessageDto(
            m.Id, m.AccountId, m.Direction.ToString(), m.Folder.ToString(), m.SentAt, m.IsRead,
            ListMyMailThreadsHandler.Party(sync.Reveal<MailParty>(m.FromEnc)),
            ListMyMailThreadsHandler.Parties(sync.Reveal<List<MailParty>>(m.ToEnc)),
            ListMyMailThreadsHandler.Parties(sync.Reveal<List<MailParty>>(m.CcEnc)),
            sync.RevealText(m.SubjectEnc),
            sync.RevealText(m.BodyEnc),
            m.BodyIsHtml,
            m.BodyTruncated,
            (sync.Reveal<List<MailAttachmentInfo>>(m.AttachmentsEnc) ?? [])
                .Select(a => new MailAttachmentDto(a.Id, a.Name, a.ContentType, a.Size, a.IsInline)).ToList(),
            MailLinks.Of(links[m.Id], linkNames))).ToList();

        return Result.Ok(new MailThreadDto(anchor.AccountId, label ?? string.Empty, dtos[^1].Subject ?? dtos[0].Subject, dtos));
    }
}

// ── « Synchroniser » ─────────────────────────────────────────────────────────────────────────

internal sealed record SyncMyMailboxCommand(Guid AccountId) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ConnectedAccount";
    public string? ResourceId => AccountId.ToString();
}

/// <summary>
/// Reads one mailbox now, IN THE BACKGROUND: a first read can take minutes and must hold neither
/// an HTTP request nor this command's transaction — the command only enqueues the job.
/// </summary>
internal sealed class SyncMyMailboxHandler(IntegrationDbContext db, ICurrentUser me, IBackgroundJobClient jobs)
    : IRequestHandler<SyncMyMailboxCommand, Result>
{
    public async Task<Result> Handle(SyncMyMailboxCommand cmd, CancellationToken ct)
    {
        var account = await db.ConnectedAccounts.FirstOrDefaultAsync(a => a.Id == cmd.AccountId && a.UserId == me.Id, ct);
        if (account is null) return Result.Fail(ConnectedAccountErrors.NotFound);
        if (!account.IsMailEnabled) return Result.Fail("CONNECTED_MAILBOX_DISABLED");

        var (tenantId, accountId) = (me.TenantId, account.Id);
        jobs.Enqueue<SyncConnectedMailboxJob>(job => job.ExecuteAsync(tenantId, accountId));
        return Result.Ok();
    }
}

// ── Endpoints ────────────────────────────────────────────────────────────────────────────────

internal static class ConnectedMailboxReadEndpoints
{
    internal static IEndpointRouteBuilder MapConnectedMailboxReadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("integration/me/mail")
            .WithTags("ConnectedMailbox")
            .RequireAuthorization(Permissions.CanUseConnectedAccounts.Code);

        group.MapGet("threads", async (Guid? accountId, bool? unreadOnly, int? page, int? pageSize, string? entity, string? search, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new ListMyMailThreadsQuery(accountId, unreadOnly ?? false, page ?? 1, pageSize ?? 25, entity, search), ct)))
            .WithName("ListMyMailThreads")
            .WithSummary("The caller's conversations kept from their mailboxes (only those with a lead or a client), latest first")
            .Produces<MailThreadPageDto>()
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("messages/{id:guid}/thread", async (Guid id, ISender sender, CancellationToken ct) =>
                await sender.Send(new GetMyMailThreadQuery(id), ct) is { IsSuccess: true } ok
                    ? Results.Ok(ok.Value)
                    : Results.Problem(title: "Messagerie", detail: GetMyMailThreadHandler.NotFound, statusCode: StatusCodes.Status404NotFound))
            .WithName("GetMyMailThread")
            .WithSummary("Every kept message of the conversation this message belongs to, oldest first — the caller's own only")
            .WithDescription("An HTML body is returned as the sender wrote it, unsanitised: render it in a sandboxed frame.")
            .Produces<MailThreadDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapPost("integration/me/connected-accounts/{id:guid}/mail/sync", async (Guid id, ISender sender, CancellationToken ct) =>
                await sender.Send(new SyncMyMailboxCommand(id), ct) is { IsSuccess: false } failed
                    ? Results.Problem(title: "Messagerie", detail: failed.Error,
                        statusCode: failed.Error == ConnectedAccountErrors.NotFound ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict)
                    : Results.Accepted())
            .WithTags("ConnectedMailbox")
            .WithName("SyncMyMailbox")
            .WithSummary("Reads the mailbox now, in the background (202); the account's mailSyncedAt moves when it is done")
            .RequireAuthorization(Permissions.CanUseConnectedAccounts.Code)
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }
}

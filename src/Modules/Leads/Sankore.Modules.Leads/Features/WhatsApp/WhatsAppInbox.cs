namespace Sankore.Modules.Leads.Features.WhatsApp;

using System.Globalization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>The tabs of the inbox (WA-FE-09).</summary>
public enum WhatsAppInboxTab
{
    /// <summary>A customer waits for a person — « À traiter ».</summary>
    ToHandle,

    /// <summary>« Bot actif ».</summary>
    Bot,

    /// <summary>An agent holds it — « Pris en charge ».</summary>
    Human,

    /// <summary>We wrote, the customer has not answered yet — « En attente du client ».</summary>
    AwaitingReply,

    /// <summary>« Clôturées ».</summary>
    Closed,
}

/// <param name="Name">The lead's or client's name, else the WhatsApp profile name.</param>
/// <param name="Preview">The last message, cut to one line — what the agent triages on.</param>
/// <param name="Route">Where the conversation opens: the lead's WhatsApp tab.</param>
public sealed record WhatsAppInboxItemDto(
    Guid ConversationId,
    Guid? LeadId,
    Guid? ClientId,
    string Name,
    string? Product,
    string Status,
    Guid? OwnerUserId,
    string? OwnerName,
    Guid? TargetAgentId,
    string? TargetAgentName,
    int UnreadCount,
    string? Preview,
    string? PreviewDirection,
    DateTimeOffset LastMessageAt,
    DateTimeOffset? WindowExpiresAt,
    DateTimeOffset? TakeoverDeadline,
    bool TakeoverBreached,
    bool AmbiguousMatch,
    string? Route);

/// <param name="Counts">Per tab, within the caller's perimeter and filters — the tabs' badges.</param>
/// <param name="NextCursor">Pass it back for the next page; null at the end.</param>
public sealed record WhatsAppInboxDto(
    IReadOnlyList<WhatsAppInboxItemDto> Items,
    IReadOnlyDictionary<string, int> Counts,
    int UnreadConversations,
    string? NextCursor);

internal sealed record GetWhatsAppInboxQuery(
    WhatsAppInboxTab Tab,
    Guid? AgentId,
    Guid? ProductLineId,
    bool UnreadOnly,
    string? Cursor,
    int Limit,
    bool HasWhatsAppPermission,
    bool MaySupervise) : IRequest<Result<WhatsAppInboxDto>>;

/// <summary>
/// The WhatsApp queue of the caller (WA-FE-09, WA-BE-19).
///
/// <para>
/// <b>The perimeter is the point.</b> An agent sees the conversations they hold, the ones waiting
/// for them, and those of the leads they work. A supervisor (<c>WhatsApp.Supervise</c>) sees their
/// team's — the agents of their agency and sub-agencies — plus the conversations nobody is
/// expected on, which are exactly the ones a supervisor must place. The bypass roles of
/// <see cref="LeadAccessGuard"/> see the tenant. The agent filter narrows inside that perimeter
/// and never widens it.
/// </para>
///
/// <para>
/// <b>Sorted by urgency</b>, as the specification asks: what waits for a person by its SLA deadline,
/// what an agent holds by the closing of its window, the rest by its last message.
/// </para>
///
/// <para>
/// <b>Two kinds of cursor, both opaque.</b> The tabs sorted by last message — the bot's, the
/// awaiting, the closed, which grow without bound — page by KEYSET (<c>k:</c> the last row's
/// instants): a new message arriving while an agent scrolls neither repeats nor skips a row, and a
/// deep page costs what the first does. The two queues a person works (« À traiter », « Pris en
/// charge ») are short and re-ranked by urgency at every read; they page by offset (<c>o:</c>).
/// </para>
/// </summary>
internal sealed class GetWhatsAppInboxHandler(
    LeadsDbContext db,
    ICurrentUser me,
    IAdministrationModule admin,
    ICustomersModule customers,
    WhatsAppPerimeter perimeter)
    : IRequestHandler<GetWhatsAppInboxQuery, Result<WhatsAppInboxDto>>
{
    public async Task<Result<WhatsAppInboxDto>> Handle(GetWhatsAppInboxQuery query, CancellationToken ct)
    {
        if (!query.HasWhatsAppPermission && !query.MaySupervise)
            return Result.Fail<WhatsAppInboxDto>(LeadAccessGuard.Forbidden);

        var scoped = await ScopeAsync(query, ct);

        // Counts first, on the whole perimeter: they are the tabs' badges and must not depend on
        // which tab is open.
        var counts = await scoped
            .GroupBy(x => x.Conversation.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var unreadConversations = await scoped.CountAsync(
            x => x.Conversation.UnreadCount > 0 && x.Conversation.Status != WhatsAppConversationStatus.Closed, ct);

        var tab = query.Tab switch
        {
            WhatsAppInboxTab.ToHandle => scoped.Where(x => x.Conversation.Status == WhatsAppConversationStatus.PendingHuman),
            WhatsAppInboxTab.Bot => scoped.Where(x => x.Conversation.Status == WhatsAppConversationStatus.Bot),
            WhatsAppInboxTab.Human => scoped.Where(x => x.Conversation.Status == WhatsAppConversationStatus.Human),
            WhatsAppInboxTab.AwaitingReply => scoped.Where(x => x.Conversation.Status == WhatsAppConversationStatus.AwaitingReply),
            _ => scoped.Where(x => x.Conversation.Status == WhatsAppConversationStatus.Closed),
        };

        if (query.UnreadOnly) tab = tab.Where(x => x.Conversation.UnreadCount > 0);

        var byRecency = query.Tab is not (WhatsAppInboxTab.ToHandle or WhatsAppInboxTab.Human);
        var cursor = InboxCursor.Decode(query.Cursor);

        if (byRecency && cursor.Keyset is { } after)
        {
            // Strictly after the last row of the previous page, in (last message, creation) order.
            tab = tab.Where(x =>
                x.Conversation.LastMessageAt < after.LastMessageAt
                || (x.Conversation.LastMessageAt == after.LastMessageAt && x.Conversation.CreatedAt < after.CreatedAt));
        }

        var ordered = query.Tab switch
        {
            WhatsAppInboxTab.ToHandle => tab
                .OrderBy(x => x.Conversation.TakeoverDeadline == null)
                .ThenBy(x => x.Conversation.TakeoverDeadline)
                .ThenBy(x => x.Conversation.PendingSince),
            WhatsAppInboxTab.Human => tab
                .OrderBy(x => x.Conversation.WindowExpiresAt == null)
                .ThenBy(x => x.Conversation.WindowExpiresAt),
            _ => tab.OrderByDescending(x => x.Conversation.LastMessageAt),
        };

        var limit = Math.Clamp(query.Limit, 1, 50);

        var paged = byRecency
            ? ordered.ThenByDescending(x => x.Conversation.CreatedAt)
            : ordered.ThenBy(x => x.Conversation.Id).Skip(cursor.Offset);

        var rows = await paged
            .Take(limit + 1)
            .Select(x => new
            {
                x.Conversation.Id,
                x.Conversation.LeadId,
                x.Conversation.ClientId,
                x.Conversation.ContactName,
                x.Conversation.Status,
                x.Conversation.OwnerUserId,
                x.Conversation.TargetAgentId,
                x.Conversation.UnreadCount,
                x.Conversation.LastMessageAt,
                x.Conversation.CreatedAt,
                x.Conversation.WindowExpiresAt,
                x.Conversation.TakeoverDeadline,
                Breached = x.Conversation.TakeoverBreachedAt != null,
                x.Conversation.AmbiguousMatch,
                LeadName = x.Lead != null ? x.Lead.FullName : null,
                Product = x.Lead != null ? x.Lead.InterestedProduct : null,
                Last = db.WhatsAppMessages
                    .Where(m => m.ConversationId == x.Conversation.Id)
                    .OrderByDescending(m => m.OccurredAt)
                    .Select(m => new { m.Body, m.Direction, m.Kind })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        var userIds = rows.SelectMany(r => new[] { r.OwnerUserId, r.TargetAgentId })
            .Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();

        var names = userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await admin.GetUserNamesAsync(me.TenantId, userIds, ct);

        var clientIds = rows.Where(r => r.LeadId is null && r.ClientId is not null).Select(r => r.ClientId!.Value).Distinct().ToList();

        var clients = clientIds.Count == 0
            ? new Dictionary<Guid, ClientSummary>()
            : await SafeClientsAsync(clientIds, ct);

        var items = rows.Select(r => new WhatsAppInboxItemDto(
            r.Id,
            r.LeadId,
            r.ClientId,
            r.LeadName
                ?? (r.ClientId is { } c && clients.TryGetValue(c, out var client) ? client.DisplayName : null)
                ?? r.ContactName
                ?? "Contact WhatsApp",
            r.Product is { Length: > 0 } p && !string.Equals(p, "Unknown", StringComparison.OrdinalIgnoreCase) ? p : null,
            r.Status.ToString(),
            r.OwnerUserId,
            r.OwnerUserId is { } o ? names.GetValueOrDefault(o) : null,
            r.TargetAgentId,
            r.TargetAgentId is { } t ? names.GetValueOrDefault(t) : null,
            r.UnreadCount,
            Preview(r.Last?.Body, r.Last?.Kind),
            r.Last?.Direction.ToString(),
            r.LastMessageAt,
            r.WindowExpiresAt,
            r.TakeoverDeadline,
            r.Breached,
            r.AmbiguousMatch,
            r.LeadId is { } lead ? $"/leads/{lead}/suivi/whatsapp" : null)).ToList();

        var countsByTab = new Dictionary<string, int>
        {
            [nameof(WhatsAppInboxTab.ToHandle)] = CountOf(WhatsAppConversationStatus.PendingHuman),
            [nameof(WhatsAppInboxTab.Bot)] = CountOf(WhatsAppConversationStatus.Bot),
            [nameof(WhatsAppInboxTab.Human)] = CountOf(WhatsAppConversationStatus.Human),
            [nameof(WhatsAppInboxTab.AwaitingReply)] = CountOf(WhatsAppConversationStatus.AwaitingReply),
            [nameof(WhatsAppInboxTab.Closed)] = CountOf(WhatsAppConversationStatus.Closed),
        };

        string? next = null;

        if (hasMore)
        {
            next = byRecency
                ? InboxCursor.EncodeKeyset(rows[^1].LastMessageAt, rows[^1].CreatedAt)
                : InboxCursor.EncodeOffset(cursor.Offset + limit);
        }

        return Result.Ok(new WhatsAppInboxDto(items, countsByTab, unreadConversations, next));

        int CountOf(WhatsAppConversationStatus status) => counts.FirstOrDefault(x => x.Status == status)?.Count ?? 0;
    }

    /// <summary>The caller's perimeter, with the lead joined and the filters applied.</summary>
    private async Task<IQueryable<WhatsAppScopedRow>> ScopeAsync(GetWhatsAppInboxQuery query, CancellationToken ct)
    {
        var rows = await perimeter.ScopeAsync(query.MaySupervise, ct);

        if (query.AgentId is { } agent)
        {
            rows = rows.Where(x =>
                x.Conversation.OwnerUserId == agent
                || x.Conversation.TargetAgentId == agent
                || (x.Lead != null && x.Lead.CurrentAssignedId == agent));
        }

        if (query.ProductLineId is { } product)
            rows = rows.Where(x => x.Lead != null && x.Lead.ProductLineId == product);

        return rows;
    }

    private async Task<IReadOnlyDictionary<Guid, ClientSummary>> SafeClientsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        try
        {
            return await customers.GetClientSummariesAsync(me.TenantId, ids, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // M01 down: the row falls back to the WhatsApp profile name rather than failing the inbox.
            _ = ex;
            return new Dictionary<Guid, ClientSummary>();
        }
    }

    private static string? Preview(string? body, WhatsAppMessageKind? kind)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            var line = body.ReplaceLineEndings(" ").Trim();
            return line.Length <= 90 ? line : line[..89] + "…";
        }

        return kind switch
        {
            WhatsAppMessageKind.Image => "Image",
            WhatsAppMessageKind.Document => "Document",
            WhatsAppMessageKind.Audio => "Message vocal",
            WhatsAppMessageKind.Video => "Vidéo",
            WhatsAppMessageKind.Location => "Position",
            null => null,
            _ => "Message",
        };
    }
}

/// <summary>
/// The inbox's opaque cursor: <c>o:{offset}</c> for the urgency queues, <c>k:{ticks}:{ticks}</c>
/// for the recency tabs. A bare number — the lot-2 shape — still reads as an offset.
/// </summary>
internal readonly record struct InboxCursor(int Offset, (DateTimeOffset LastMessageAt, DateTimeOffset CreatedAt)? Keyset)
{
    public static InboxCursor Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return new(0, null);

        var parts = cursor.Split(':');

        if (parts is ["k", var last, var created]
            && long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var l)
            && long.TryParse(created, NumberStyles.None, CultureInfo.InvariantCulture, out var c)
            && l is >= 0 && l <= DateTimeOffset.MaxValue.UtcTicks
            && c is >= 0 && c <= DateTimeOffset.MaxValue.UtcTicks)
        {
            return new(0, (new DateTimeOffset(l, TimeSpan.Zero), new DateTimeOffset(c, TimeSpan.Zero)));
        }

        var offset = parts is ["o", var o] ? o : cursor;
        return new(int.TryParse(offset, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : 0, null);
    }

    public static string EncodeOffset(int offset) => string.Create(CultureInfo.InvariantCulture, $"o:{offset}");

    public static string EncodeKeyset(DateTimeOffset lastMessageAt, DateTimeOffset createdAt) =>
        string.Create(CultureInfo.InvariantCulture, $"k:{lastMessageAt.UtcTicks}:{createdAt.UtcTicks}");
}

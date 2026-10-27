namespace Sankore.Modules.Integration.Features.ConnectedAccounts;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using MimeKit;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Kernel;

// Connected mailbox, lot 2: reading a mailbox, one provider at a time. A reader answers HEADERS
// (who, when, read or not) for everything that changed, and CONTENT only for the messages the sync
// decided to keep — the rest of the mailbox is never fetched in full.

/// <summary>An address and the name shown with it.</summary>
internal sealed record MailParty(string Address, string? Name);

/// <summary>What a reader says about one message before anybody decides to keep it.</summary>
internal sealed record MailHeader(
    string ExternalId,
    MailFolderKind Folder,
    string ThreadKey,
    string? InternetMessageId,
    MailParty? From,
    IReadOnlyList<MailParty> To,
    IReadOnlyList<MailParty> Cc,
    DateTimeOffset SentAt,
    bool IsRead,
    string? Subject,
    string? Snippet)
{
    public IEnumerable<MailParty> Parties => (From is null ? To : To.Prepend(From)).Concat(Cc);
}

/// <param name="Id">The provider's attachment id (Graph, Gmail) or the part's index (IMAP).</param>
internal sealed record MailAttachmentInfo(string Id, string Name, string? ContentType, long Size, bool IsInline);

internal sealed record MailContent(string? Body, bool IsHtml, IReadOnlyList<MailAttachmentInfo> Attachments);

/// <param name="Cursor">Where the next read starts — persisted after the page is processed.</param>
/// <param name="HasMore">Read again at once: a full window or a backlog is several pages.</param>
internal sealed record MailChangePage(IReadOnlyList<MailHeader> Upserts, IReadOnlyList<string> Removed, string? Cursor, bool HasMore)
{
    public static MailChangePage Empty(string? cursor) => new([], [], cursor, false);
}

/// <summary>A file attached to an outgoing message.</summary>
internal sealed record OutgoingAttachment(string Name, string ContentType, byte[] Content);

/// <summary>The kept message being answered: what threads the reply at the provider and in the recipient's client.</summary>
internal sealed record OutgoingReplyTo(string ExternalId, string? InternetMessageId, string ThreadKey);

/// <summary>A message to send from a connected mailbox (lot 3). Bodies are HTML; a text part is derived.</summary>
internal sealed record OutgoingMail(
    MailParty From,
    IReadOnlyList<MailParty> To,
    IReadOnlyList<MailParty> Cc,
    IReadOnlyList<MailParty> Bcc,
    string Subject,
    string HtmlBody,
    IReadOnlyList<OutgoingAttachment> Attachments,
    OutgoingReplyTo? ReplyTo);

/// <summary>
/// The provider refused to send (lot 3). <see cref="Code"/> is ours — <c>MAIL_SEND_REFUSED</c>
/// (the provider said no: a recipient, a size, a policy) or <c>MAIL_SEND_FAILED</c> (it could not
/// be reached) — never the provider's text, which may echo the message.
/// </summary>
internal sealed class MailSendException(string code) : Exception(code)
{
    public const string Refused = "MAIL_SEND_REFUSED";
    public const string Failed = "MAIL_SEND_FAILED";
    public string Code { get; } = code;
}

/// <summary>The provider no longer knows the cursor (Graph 410, Gmail history 404, IMAP UIDVALIDITY changed).</summary>
internal sealed class MailCursorExpiredException() : Exception("mail_cursor_expired");

/// <summary>A connection open for one sync run: a token, or a logged-in IMAP client.</summary>
internal abstract class MailboxSession : IAsyncDisposable
{
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal interface IMailboxReader
{
    bool CanRead(ConnectedAccount account);

    /// <summary>The parts of a mailbox read one cursor each.</summary>
    IReadOnlyList<string> Streams { get; }

    /// <summary>Fails with a code — <c>CONNECTED_ACCOUNT_NEEDS_RECONNECT</c> or <c>MAIL_IMAP_AUTH_FAILED</c> mean the user must act.</summary>
    Task<Result<MailboxSession>> OpenAsync(ConnectedAccount account, CancellationToken ct);

    /// <param name="since">The start of the window, used when <paramref name="cursor"/> is null.</param>
    Task<MailChangePage> ReadChangesAsync(MailboxSession session, string stream, string? cursor, DateTimeOffset since, CancellationToken ct);

    /// <summary>Null when the message is gone (deleted between the two reads).</summary>
    Task<MailContent?> ReadContentAsync(MailboxSession session, string externalId, CancellationToken ct);

    // ── Writing (lot 3) ──────────────────────────────────────────────────

    /// <summary>Sends from the account, so the message lands in its own « Envoyés » too. Throws <see cref="MailSendException"/>.</summary>
    Task SendAsync(MailboxSession session, ConnectedAccount account, OutgoingMail mail, CancellationToken ct);

    Task SetReadAsync(MailboxSession session, string externalId, bool isRead, CancellationToken ct);

    /// <summary>The attachment's bytes, or null when the message or the attachment is gone.</summary>
    Task<byte[]?> ReadAttachmentAsync(MailboxSession session, string externalId, string attachmentId, CancellationToken ct);
}

/// <summary>
/// The MIME message Gmail and SMTP send. A reply carries In-Reply-To and References, which is what
/// threads it in the RECIPIENT's mail client — the provider's own thread id only threads it for us.
/// </summary>
internal static class OutgoingMime
{
    public static MimeMessage Build(OutgoingMail mail)
    {
        var message = new MimeMessage { Subject = mail.Subject };
        message.From.Add(new MailboxAddress(mail.From.Name, mail.From.Address));
        message.To.AddRange(mail.To.Select(p => new MailboxAddress(p.Name, p.Address)));
        message.Cc.AddRange(mail.Cc.Select(p => new MailboxAddress(p.Name, p.Address)));
        message.Bcc.AddRange(mail.Bcc.Select(p => new MailboxAddress(p.Name, p.Address)));

        if (mail.ReplyTo?.InternetMessageId is { Length: > 0 } inReplyTo)
        {
            message.InReplyTo = inReplyTo;
            // The IMAP thread key is the conversation's first Message-ID: the start of References.
            if (mail.ReplyTo.ThreadKey.StartsWith('<') && mail.ReplyTo.ThreadKey != inReplyTo) message.References.Add(mail.ReplyTo.ThreadKey);
            message.References.Add(inReplyTo);
        }

        var body = new BodyBuilder { HtmlBody = mail.HtmlBody, TextBody = MailText.FromHtml(mail.HtmlBody) };
        foreach (var a in mail.Attachments) body.Attachments.Add(a.Name, a.Content, ContentType.Parse(a.ContentType));

        message.Body = body.ToMessageBody();
        return message;
    }

    public static byte[] ToBytes(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(FormatOptions.Default, stream);
        return stream.ToArray();
    }
}

internal static class MailText
{
    /// <summary>Readable text from HTML — for a text/plain part and a preview, never for display.</summary>
    public static string FromHtml(string html)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(html, @"<(br|/p|/div|/li|/tr|/h[1-6])\b[^>]*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text);
        return System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]+\n", "\n").Trim();
    }
}

internal sealed class TokenMailboxSession(string accessToken) : MailboxSession
{
    public string AccessToken { get; } = accessToken;
}

/// <summary>The small JSON-over-HTTP conversation Graph and Gmail share.</summary>
internal static class MailHttp
{
    public static async Task<(HttpStatusCode Status, JsonElement? Body)> GetAsync(
        HttpClient http, string url, string accessToken, CancellationToken ct, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            throw new ProviderAuthException("provider_unreachable", revoked: false);
        }

        using (response)
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                return (response.StatusCode, doc.RootElement.Clone());
            }
            catch (JsonException)
            {
                return (response.StatusCode, null);
            }
        }
    }

    /// <summary>A JSON write (POST, PATCH, DELETE) whose status the caller reads.</summary>
    public static async Task<(HttpStatusCode Status, JsonElement? Body)> SendJsonAsync(
        HttpClient http, HttpMethod method, string url, string accessToken, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await ReadAsync(http, request, ct);
    }

    public static async Task<(HttpStatusCode Status, JsonElement? Body)> ReadAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new MailSendException(MailSendException.Failed);
        }

        using (response)
        {
            try
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(text)) return (response.StatusCode, null);
                using var doc = JsonDocument.Parse(text);
                return (response.StatusCode, doc.RootElement.Clone());
            }
            catch (JsonException)
            {
                return (response.StatusCode, null);
            }
        }
    }

    /// <summary>What a refused write becomes: a refused token asks for a reconnection, a refusal is the provider's, the rest a failure.</summary>
    public static Exception WriteFailure(string provider, HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => new ProviderAuthException($"{provider}_http_401", revoked: true),
        >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError and not HttpStatusCode.TooManyRequests
            => new MailSendException(MailSendException.Refused),
        _ => new MailSendException(MailSendException.Failed),
    };

    /// <summary>
    /// A refusal the account cannot recover from by itself (401: the token was refused) asks for a
    /// reconnection; anything else is a failed run the next one retries.
    /// </summary>
    public static ProviderAuthException Failure(string provider, HttpStatusCode status) =>
        new($"{provider}_http_{(int)status}", revoked: status == HttpStatusCode.Unauthorized);

    public static string? String(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static async Task<Result<MailboxSession>> OpenWithTokenAsync(ConnectedAccountTokens tokens, ConnectedAccount account, CancellationToken ct)
    {
        var token = await tokens.GetAccessTokenAsync(account, ct);
        return token.IsSuccess
            ? Result.Ok<MailboxSession>(new TokenMailboxSession(token.Value!))
            : Result.Fail<MailboxSession>(token.Error!);
    }
}

// ── Microsoft Graph ──────────────────────────────────────────────────────────────────────────

/// <summary>
/// Outlook through Graph: one delta query per folder (Inbox, Sent Items). The first read is
/// bounded by <c>receivedDateTime ge</c> — the one filter Graph accepts on a message delta — and
/// the delta link it ends with reads only what changed afterwards, deletions and « lu » included.
/// </summary>
internal sealed class GraphMailboxReader(HttpClient http, ConnectedAccountTokens tokens) : IMailboxReader
{
    private const string Select =
        "id,conversationId,internetMessageId,subject,bodyPreview,from,toRecipients,ccRecipients,receivedDateTime,sentDateTime,isRead";

    public bool CanRead(ConnectedAccount account) =>
        account.Provider == ConnectedAccountProvider.Microsoft && !FakeConnectedAccountProvider.IsSimulated(account);

    public IReadOnlyList<string> Streams { get; } = ["inbox", "sentitems"];

    public Task<Result<MailboxSession>> OpenAsync(ConnectedAccount account, CancellationToken ct) =>
        MailHttp.OpenWithTokenAsync(tokens, account, ct);

    public async Task<MailChangePage> ReadChangesAsync(MailboxSession session, string stream, string? cursor, DateTimeOffset since, CancellationToken ct)
    {
        var token = ((TokenMailboxSession)session).AccessToken;
        var url = cursor ?? $"https://graph.microsoft.com/v1.0/me/mailFolders/{stream}/messages/delta"
                            + $"?$select={Select}&$filter=receivedDateTime ge {since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";

        var (status, body) = await MailHttp.GetAsync(http, url, token, ct, ("Prefer", "odata.maxpagesize=50"));

        if (status == HttpStatusCode.Gone || (status == HttpStatusCode.BadRequest && cursor is not null && IsSyncStateError(body)))
            throw new MailCursorExpiredException();
        if ((int)status >= 300 || body is null) throw MailHttp.Failure("graph", status);

        var folder = stream == "sentitems" ? MailFolderKind.Sent : MailFolderKind.Inbox;
        var upserts = new List<MailHeader>();
        var removed = new List<string>();

        foreach (var m in body.Value.GetProperty("value").EnumerateArray())
        {
            var id = m.String("id");
            if (id is null) continue;

            if (m.TryGetProperty("@removed", out _))
            {
                removed.Add(id);
                continue;
            }

            var sentAt = Date(m.String(folder == MailFolderKind.Sent ? "sentDateTime" : "receivedDateTime"))
                         ?? Date(m.String("receivedDateTime")) ?? DateTimeOffset.UtcNow;

            upserts.Add(new MailHeader(
                id, folder, m.String("conversationId") ?? id, m.String("internetMessageId"),
                Party(m, "from"), Parties(m, "toRecipients"), Parties(m, "ccRecipients"), sentAt,
                m.TryGetProperty("isRead", out var r) && r.ValueKind == JsonValueKind.True,
                m.String("subject"), m.String("bodyPreview")));
        }

        var next = body.Value.String("@odata.nextLink");
        return next is not null
            ? new MailChangePage(upserts, removed, next, HasMore: true)
            : new MailChangePage(upserts, removed, body.Value.String("@odata.deltaLink"), HasMore: false);
    }

    public async Task<MailContent?> ReadContentAsync(MailboxSession session, string externalId, CancellationToken ct)
    {
        var token = ((TokenMailboxSession)session).AccessToken;
        var id = Uri.EscapeDataString(externalId);

        var (status, body) = await MailHttp.GetAsync(http, $"https://graph.microsoft.com/v1.0/me/messages/{id}?$select=body,hasAttachments",
            token, ct, ("Prefer", "outlook.body-content-type=\"html\""));

        if (status == HttpStatusCode.NotFound) return null;
        if ((int)status >= 300 || body is null) throw MailHttp.Failure("graph", status);

        var content = body.Value.TryGetProperty("body", out var b) ? b : default;
        var attachments = new List<MailAttachmentInfo>();

        if (body.Value.TryGetProperty("hasAttachments", out var has) && has.ValueKind == JsonValueKind.True)
        {
            var (aStatus, aBody) = await MailHttp.GetAsync(http,
                $"https://graph.microsoft.com/v1.0/me/messages/{id}/attachments?$select=id,name,contentType,size,isInline", token, ct);

            if ((int)aStatus < 300 && aBody is not null)
            {
                foreach (var a in aBody.Value.GetProperty("value").EnumerateArray())
                {
                    attachments.Add(new MailAttachmentInfo(
                        a.String("id") ?? string.Empty, a.String("name") ?? "pièce jointe", a.String("contentType"),
                        a.TryGetProperty("size", out var size) && size.TryGetInt64(out var n) ? n : 0,
                        a.TryGetProperty("isInline", out var inline) && inline.ValueKind == JsonValueKind.True));
                }
            }
        }

        return new MailContent(content.String("content"), !string.Equals(content.String("contentType"), "text", StringComparison.OrdinalIgnoreCase), attachments);
    }

    // ── Writing (lot 3) ──────────────────────────────────────────────────

    /// <summary>
    /// Graph's request limit is 4 MB, so an attachment larger than this goes through an upload
    /// session instead of the draft's attachments collection.
    /// </summary>
    internal const int InlineAttachmentMax = 3 * 1024 * 1024;

    /// <summary>Upload-session chunks must be multiples of 320 KiB; eleven of them stay under 4 MB.</summary>
    internal const int UploadChunk = 11 * 320 * 1024;

    private const string Graph = "https://graph.microsoft.com/v1.0/me";

    /// <summary>
    /// Always through a DRAFT — a new one, or <c>createReply</c> on the answered message, which is
    /// what keeps the reply in its conversation (Graph refuses In-Reply-To as a custom header). The
    /// draft then takes the agent's text and recipients, its attachments, and is sent; a failure on
    /// the way deletes it rather than leaving a half-written draft in the user's Outlook.
    /// </summary>
    public async Task SendAsync(MailboxSession session, ConnectedAccount account, OutgoingMail mail, CancellationToken ct)
    {
        var token = ((TokenMailboxSession)session).AccessToken;
        var content = new Dictionary<string, object?>
        {
            ["subject"] = mail.Subject,
            ["body"] = new { contentType = "HTML", content = mail.HtmlBody },
            ["toRecipients"] = Recipients(mail.To),
            ["ccRecipients"] = Recipients(mail.Cc),
            ["bccRecipients"] = Recipients(mail.Bcc),
        };

        string? draftId = null;

        if (mail.ReplyTo is { } reply)
        {
            var (status, created) = await MailHttp.SendJsonAsync(http, HttpMethod.Post,
                $"{Graph}/messages/{Uri.EscapeDataString(reply.ExternalId)}/createReply", token, new { }, ct);

            // The answered message was deleted meanwhile: the reply goes out as a new message.
            if ((int)status < 300) draftId = created?.String("id");
            else if (status != HttpStatusCode.NotFound) throw MailHttp.WriteFailure("graph", status);

            if (draftId is not null)
            {
                var (patched, _) = await MailHttp.SendJsonAsync(http, HttpMethod.Patch, $"{Graph}/messages/{Uri.EscapeDataString(draftId)}", token, content, ct);
                if ((int)patched >= 300) await FailAsync(token, draftId, patched, ct);
            }
        }

        if (draftId is null)
        {
            var (status, created) = await MailHttp.SendJsonAsync(http, HttpMethod.Post, $"{Graph}/messages", token, content, ct);
            if ((int)status >= 300 || created?.String("id") is not { } id) throw MailHttp.WriteFailure("graph", status);
            draftId = id;
        }

        var draft = Uri.EscapeDataString(draftId);

        foreach (var attachment in mail.Attachments)
        {
            var status = attachment.Content.Length <= InlineAttachmentMax
                ? (await MailHttp.SendJsonAsync(http, HttpMethod.Post, $"{Graph}/messages/{draft}/attachments", token, new Dictionary<string, object>
                {
                    ["@odata.type"] = "#microsoft.graph.fileAttachment",
                    ["name"] = attachment.Name,
                    ["contentType"] = attachment.ContentType,
                    ["contentBytes"] = Convert.ToBase64String(attachment.Content),
                }, ct)).Status
                : await UploadAsync(token, draft, attachment, ct);

            if ((int)status >= 300) await FailAsync(token, draftId, status, ct);
        }

        var (sent, _) = await MailHttp.SendJsonAsync(http, HttpMethod.Post, $"{Graph}/messages/{draft}/send", token, null, ct);
        if ((int)sent >= 300) await FailAsync(token, draftId, sent, ct);
    }

    private async Task<HttpStatusCode> UploadAsync(string token, string draft, OutgoingAttachment attachment, CancellationToken ct)
    {
        var (status, session) = await MailHttp.SendJsonAsync(http, HttpMethod.Post, $"{Graph}/messages/{draft}/attachments/createUploadSession", token,
            new { AttachmentItem = new { attachmentType = "file", name = attachment.Name, size = attachment.Content.Length, contentType = attachment.ContentType } }, ct);

        if ((int)status >= 300 || session?.String("uploadUrl") is not { } uploadUrl) return status;

        for (var offset = 0; offset < attachment.Content.Length; offset += UploadChunk)
        {
            var length = Math.Min(UploadChunk, attachment.Content.Length - offset);

            // The upload URL is pre-authorised: sending the bearer token with it is refused.
            using var put = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
            {
                Content = new ByteArrayContent(attachment.Content, offset, length),
            };
            put.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, attachment.Content.Length);

            var (chunk, _) = await MailHttp.ReadAsync(http, put, ct);
            if ((int)chunk >= 300) return chunk;
        }

        return HttpStatusCode.OK;
    }

    private async Task FailAsync(string token, string draftId, HttpStatusCode status, CancellationToken ct)
    {
        try
        {
            await MailHttp.SendJsonAsync(http, HttpMethod.Delete, $"{Graph}/messages/{Uri.EscapeDataString(draftId)}", token, null, ct);
        }
        catch (MailSendException)
        {
            // Best effort: a draft left behind is the lesser harm.
        }

        throw MailHttp.WriteFailure("graph", status);
    }

    private static object[] Recipients(IEnumerable<MailParty> parties) =>
        parties.Select(p => (object)new { emailAddress = new { address = p.Address, name = p.Name } }).ToArray();

    public async Task SetReadAsync(MailboxSession session, string externalId, bool isRead, CancellationToken ct)
    {
        var (status, _) = await MailHttp.SendJsonAsync(http, HttpMethod.Patch, $"{Graph}/messages/{Uri.EscapeDataString(externalId)}",
            ((TokenMailboxSession)session).AccessToken, new { isRead }, ct);

        if ((int)status >= 300 && status != HttpStatusCode.NotFound) throw MailHttp.WriteFailure("graph", status);
    }

    public async Task<byte[]?> ReadAttachmentAsync(MailboxSession session, string externalId, string attachmentId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{Graph}/messages/{Uri.EscapeDataString(externalId)}/attachments/{Uri.EscapeDataString(attachmentId)}/$value");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ((TokenMailboxSession)session).AccessToken);

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw MailHttp.Failure("graph", response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private static bool IsSyncStateError(JsonElement? body) =>
        body is { } b && b.TryGetProperty("error", out var e) && e.String("code") is { } code
        && code.Contains("SyncState", StringComparison.OrdinalIgnoreCase);

    private static MailParty? Party(JsonElement m, string name) =>
        m.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object ? Recipient(p) : null;

    private static IReadOnlyList<MailParty> Parties(JsonElement m, string name) =>
        m.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(Recipient).OfType<MailParty>().ToList()
            : [];

    private static MailParty? Recipient(JsonElement r) =>
        r.TryGetProperty("emailAddress", out var e) && e.String("address") is { Length: > 0 } address
            ? new MailParty(address.Trim(), e.String("name"))
            : null;

    private static DateTimeOffset? Date(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}

// ── Gmail ────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Gmail through its REST API. One stream for the whole mailbox, because Gmail's history covers
/// every label at once. The cursor says where it stands:
/// <list type="bullet">
/// <item><c>L|historyId|pageToken</c> — the first read, listing the window page by page; the
/// history id was taken BEFORE listing, so nothing that arrives meanwhile is missed.</item>
/// <item><c>H|historyId|pageToken</c> — what changed since, from <c>history.list</c>.</item>
/// </list>
/// Spam, trash, drafts and chats are never read; a message moved to the trash counts as removed.
/// </summary>
internal sealed class GmailMailboxReader(HttpClient http, ConnectedAccountTokens tokens) : IMailboxReader
{
    private const string Api = "https://gmail.googleapis.com/gmail/v1/users/me";
    private const int MaxHistoryPages = 10;
    private static readonly string[] Ignored = ["SPAM", "TRASH", "DRAFT", "CHAT"];

    public bool CanRead(ConnectedAccount account) =>
        account.Provider == ConnectedAccountProvider.Google && !FakeConnectedAccountProvider.IsSimulated(account);

    public IReadOnlyList<string> Streams { get; } = ["all"];

    public Task<Result<MailboxSession>> OpenAsync(ConnectedAccount account, CancellationToken ct) =>
        MailHttp.OpenWithTokenAsync(tokens, account, ct);

    public async Task<MailChangePage> ReadChangesAsync(MailboxSession session, string stream, string? cursor, DateTimeOffset since, CancellationToken ct)
    {
        var token = ((TokenMailboxSession)session).AccessToken;

        if (cursor is null)
        {
            var (status, profile) = await MailHttp.GetAsync(http, $"{Api}/profile", token, ct);
            if ((int)status >= 300 || profile?.String("historyId") is not { } start) throw MailHttp.Failure("gmail", status);
            cursor = $"L|{start}|";
        }

        var parts = cursor.Split('|');
        return parts[0] == "L"
            ? await ListAsync(token, parts[1], parts.ElementAtOrDefault(2), since, ct)
            : await HistoryAsync(token, parts[1], parts.ElementAtOrDefault(2), ct);
    }

    private async Task<MailChangePage> ListAsync(string token, string historyId, string? pageToken, DateTimeOffset since, CancellationToken ct)
    {
        var q = Uri.EscapeDataString($"after:{since.ToUnixTimeSeconds()} -in:spam -in:trash -in:drafts -in:chats");
        var url = $"{Api}/messages?maxResults=100&q={q}" + (string.IsNullOrEmpty(pageToken) ? string.Empty : $"&pageToken={Uri.EscapeDataString(pageToken)}");

        var (status, body) = await MailHttp.GetAsync(http, url, token, ct);
        if ((int)status >= 300 || body is null) throw MailHttp.Failure("gmail", status);

        var ids = body.Value.TryGetProperty("messages", out var list)
            ? list.EnumerateArray().Select(m => m.String("id")).OfType<string>().ToList()
            : [];

        var upserts = new List<MailHeader>();
        foreach (var id in ids)
        {
            if (await HeaderAsync(token, id, ct) is { } header) upserts.Add(header);
        }

        // Listing done: the history from the id taken before the listing comes next, in the same run.
        var next = body.Value.String("nextPageToken");
        return new MailChangePage(upserts, [], next is null ? $"H|{historyId}|" : $"L|{historyId}|{next}", HasMore: true);
    }

    private async Task<MailChangePage> HistoryAsync(string token, string startHistoryId, string? pageToken, CancellationToken ct)
    {
        var touched = new HashSet<string>(StringComparer.Ordinal);
        var removed = new HashSet<string>(StringComparer.Ordinal);
        string? latest = null;

        for (var page = 0; page < MaxHistoryPages; page++)
        {
            var url = $"{Api}/history?startHistoryId={Uri.EscapeDataString(startHistoryId)}&maxResults=500"
                      + "&historyTypes=messageAdded&historyTypes=messageDeleted&historyTypes=labelAdded&historyTypes=labelRemoved"
                      + (string.IsNullOrEmpty(pageToken) ? string.Empty : $"&pageToken={Uri.EscapeDataString(pageToken)}");

            var (status, body) = await MailHttp.GetAsync(http, url, token, ct);
            if (status == HttpStatusCode.NotFound) throw new MailCursorExpiredException();
            if ((int)status >= 300 || body is null) throw MailHttp.Failure("gmail", status);

            if (body.Value.TryGetProperty("history", out var history))
            {
                foreach (var h in history.EnumerateArray())
                {
                    Collect(h, "messagesAdded", touched);
                    Collect(h, "labelsAdded", touched);
                    Collect(h, "labelsRemoved", touched);
                    Collect(h, "messagesDeleted", removed);
                }
            }

            latest = body.Value.String("historyId") ?? latest;
            pageToken = body.Value.String("nextPageToken");
            if (pageToken is null) break;
        }

        var upserts = new List<MailHeader>();
        foreach (var id in touched.Except(removed))
        {
            if (await HeaderAsync(token, id, ct) is { } header) upserts.Add(header);
            else removed.Add(id);
        }

        return pageToken is null
            ? new MailChangePage(upserts, removed.ToList(), $"H|{latest ?? startHistoryId}|", HasMore: false)
            : new MailChangePage(upserts, removed.ToList(), $"H|{startHistoryId}|{pageToken}", HasMore: true);
    }

    private static void Collect(JsonElement h, string kind, HashSet<string> into)
    {
        if (!h.TryGetProperty(kind, out var items)) return;
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("message", out var m) && m.String("id") is { } id) into.Add(id);
        }
    }

    /// <summary>Null for a message gone, or in spam/trash/drafts — which the sync treats as removed.</summary>
    private async Task<MailHeader?> HeaderAsync(string token, string id, CancellationToken ct)
    {
        var url = $"{Api}/messages/{Uri.EscapeDataString(id)}?format=metadata"
                  + "&metadataHeaders=From&metadataHeaders=To&metadataHeaders=Cc&metadataHeaders=Subject&metadataHeaders=Message-ID";

        var (status, m) = await MailHttp.GetAsync(http, url, token, ct);
        if (status == HttpStatusCode.NotFound) return null;
        if ((int)status >= 300 || m is null) throw MailHttp.Failure("gmail", status);

        var labels = m.Value.TryGetProperty("labelIds", out var l)
            ? l.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : [];

        if (labels.Overlaps(Ignored)) return null;

        var headers = m.Value.TryGetProperty("payload", out var payload) && payload.TryGetProperty("headers", out var hs)
            ? hs.EnumerateArray()
                .Select(x => (Name: x.String("name") ?? string.Empty, Value: x.String("value") ?? string.Empty))
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var sentAt = long.TryParse(m.Value.String("internalDate"), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.UtcNow;
        var snippet = m.Value.String("snippet");

        return new MailHeader(
            id,
            labels.Contains("SENT") ? MailFolderKind.Sent : MailFolderKind.Inbox,
            m.Value.String("threadId") ?? id,
            headers.GetValueOrDefault("Message-ID"),
            MailAddresses.Parse(headers.GetValueOrDefault("From")).FirstOrDefault(),
            MailAddresses.Parse(headers.GetValueOrDefault("To")),
            MailAddresses.Parse(headers.GetValueOrDefault("Cc")),
            sentAt,
            !labels.Contains("UNREAD"),
            headers.GetValueOrDefault("Subject"),
            snippet is null ? null : WebUtility.HtmlDecode(snippet));
    }

    public async Task<MailContent?> ReadContentAsync(MailboxSession session, string externalId, CancellationToken ct)
    {
        var token = ((TokenMailboxSession)session).AccessToken;
        var (status, m) = await MailHttp.GetAsync(http, $"{Api}/messages/{Uri.EscapeDataString(externalId)}?format=full", token, ct);

        if (status == HttpStatusCode.NotFound) return null;
        if ((int)status >= 300 || m is null || !m.Value.TryGetProperty("payload", out var payload)) throw MailHttp.Failure("gmail", status);

        string? html = null, text = null;
        var attachments = new List<MailAttachmentInfo>();
        Walk(payload, ref html, ref text, attachments);

        return new MailContent(html ?? text, html is not null, attachments);
    }

    // ── Writing (lot 3) ──────────────────────────────────────────────────

    /// <summary>
    /// The MIME message through the UPLOAD endpoint (35 MB, against the metadata endpoint's few):
    /// a reply as multipart/related with its thread id, so Gmail files it in the conversation.
    /// </summary>
    public async Task SendAsync(MailboxSession session, ConnectedAccount account, OutgoingMail mail, CancellationToken ct)
    {
        var token = ((TokenMailboxSession)session).AccessToken;
        var raw = OutgoingMime.ToBytes(OutgoingMime.Build(mail));

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://gmail.googleapis.com/upload/gmail/v1/users/me/messages/send?uploadType={(mail.ReplyTo is null ? "media" : "multipart")}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var message = new ByteArrayContent(raw);
        message.Headers.ContentType = new MediaTypeHeaderValue("message/rfc822");

        if (mail.ReplyTo is { } reply)
        {
            var related = new MultipartContent("related");
            related.Add(new StringContent(JsonSerializer.Serialize(new { threadId = reply.ThreadKey }), Encoding.UTF8, "application/json"));
            related.Add(message);
            request.Content = related;
        }
        else
        {
            request.Content = message;
        }

        var (status, _) = await MailHttp.ReadAsync(http, request, ct);
        if ((int)status >= 300) throw MailHttp.WriteFailure("gmail", status);
    }

    public async Task SetReadAsync(MailboxSession session, string externalId, bool isRead, CancellationToken ct)
    {
        object body = isRead ? new { removeLabelIds = new[] { "UNREAD" } } : new { addLabelIds = new[] { "UNREAD" } };
        var (status, _) = await MailHttp.SendJsonAsync(http, HttpMethod.Post, $"{Api}/messages/{Uri.EscapeDataString(externalId)}/modify",
            ((TokenMailboxSession)session).AccessToken, body, ct);

        if ((int)status >= 300 && status != HttpStatusCode.NotFound) throw MailHttp.WriteFailure("gmail", status);
    }

    public async Task<byte[]?> ReadAttachmentAsync(MailboxSession session, string externalId, string attachmentId, CancellationToken ct)
    {
        var (status, body) = await MailHttp.GetAsync(http,
            $"{Api}/messages/{Uri.EscapeDataString(externalId)}/attachments/{Uri.EscapeDataString(attachmentId)}",
            ((TokenMailboxSession)session).AccessToken, ct);

        if (status == HttpStatusCode.NotFound) return null;
        if ((int)status >= 300 || body?.String("data") is not { } data) throw MailHttp.Failure("gmail", status);
        return DecodeBytes(data);
    }

    private static void Walk(JsonElement part, ref string? html, ref string? text, List<MailAttachmentInfo> attachments)
    {
        var mime = part.String("mimeType") ?? string.Empty;
        var filename = part.String("filename");
        var body = part.TryGetProperty("body", out var b) ? b : default;

        if (!string.IsNullOrEmpty(filename))
        {
            var disposition = part.TryGetProperty("headers", out var hs)
                ? hs.EnumerateArray().FirstOrDefault(h => string.Equals(h.String("name"), "Content-Disposition", StringComparison.OrdinalIgnoreCase)).String("value")
                : null;

            attachments.Add(new MailAttachmentInfo(
                body.String("attachmentId") ?? string.Empty, filename, mime,
                body.ValueKind == JsonValueKind.Object && body.TryGetProperty("size", out var size) && size.TryGetInt64(out var n) ? n : 0,
                disposition?.StartsWith("inline", StringComparison.OrdinalIgnoreCase) == true));
        }
        else if (body.String("data") is { } data)
        {
            if (mime.Equals("text/html", StringComparison.OrdinalIgnoreCase)) html ??= Decode(data);
            else if (mime.Equals("text/plain", StringComparison.OrdinalIgnoreCase)) text ??= Decode(data);
        }

        if (part.TryGetProperty("parts", out var parts))
        {
            foreach (var child in parts.EnumerateArray()) Walk(child, ref html, ref text, attachments);
        }
    }

    private static string Decode(string base64Url) => Encoding.UTF8.GetString(DecodeBytes(base64Url));

    private static byte[] DecodeBytes(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }
}

/// <summary>Address headers, parsed by MimeKit — never by hand, quoted names and encoded words included.</summary>
internal static class MailAddresses
{
    /// <summary>
    /// Loose on purpose: in strict mode ONE malformed entry (« undisclosed-recipients », a bare
    /// word) makes MimeKit refuse the whole header — and a message whose To cannot be read is a
    /// message no lead can be matched to. What is not an address is dropped afterwards instead.
    /// </summary>
    private static readonly ParserOptions Loose = new()
    {
        AddressParserComplianceMode = RfcComplianceMode.Looser,
        AllowAddressesWithoutDomain = true,
    };

    public static IReadOnlyList<MailParty> Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) || !InternetAddressList.TryParse(Loose, header, out var list)) return [];
        return From(list);
    }

    public static IReadOnlyList<MailParty> From(InternetAddressList? list) =>
        list?.Mailboxes
            .Where(m => !string.IsNullOrWhiteSpace(m.Address) && m.Address.Contains('@'))
            .Select(m => new MailParty(m.Address.Trim(), string.IsNullOrWhiteSpace(m.Name) ? null : m.Name))
            .ToList()
        ?? [];
}

// ── IMAP ─────────────────────────────────────────────────────────────────────────────────────

internal sealed class ImapMailboxSession(ImapClient client, MailServerConnector connector) : MailboxSession
{
    public ImapClient Client { get; } = client;

    /// <summary>The folders opened during the run, by stream.</summary>
    public Dictionary<string, IMailFolder?> Folders { get; } = new(StringComparer.Ordinal);

    public override async ValueTask DisposeAsync()
    {
        await connector.DisconnectQuietlyAsync(Client);
        Client.Dispose();
    }
}

/// <summary>
/// Any other mailbox, through IMAP: the Inbox and the Sent folder, each with a cursor
/// « uidvalidity:last uid ». New messages only — IMAP has no cheap way to list what was deleted or
/// re-flagged without walking every kept message, so in v1 a message kept stays as it was read
/// (a CONDSTORE pass is the later improvement). A changed UIDVALIDITY means the server renumbered
/// the folder: the window is read again and kept messages are recognised by id.
/// </summary>
internal sealed class ImapMailboxReader(IntegrationDbContext db, ISecretsModule secrets, MailServerConnector connector) : IMailboxReader
{
    private const int BatchSize = 100;

    /// <summary>Where a server without SPECIAL-USE keeps sent mail, by the names clients actually use.</summary>
    private static readonly string[] SentNames =
        ["Sent", "Sent Items", "Sent Messages", "Sent Mail", "INBOX.Sent", "INBOX/Sent", "Envoyés", "Éléments envoyés", "Messages envoyés"];

    public bool CanRead(ConnectedAccount account) => account.Provider == ConnectedAccountProvider.Imap;

    public IReadOnlyList<string> Streams { get; } = ["inbox", "sent"];

    public async Task<Result<MailboxSession>> OpenAsync(ConnectedAccount account, CancellationToken ct)
    {
        var server = await db.MailServers.FirstOrDefaultAsync(m => m.AccountId == account.Id, ct);
        var password = await ConnectedAccountSecrets.OutsideTransactionAsync(() =>
            secrets.GetValueAsync(ConnectedAccountSecrets.MailPassword(account.TenantId, account.Id), ct));

        if (server is null || string.IsNullOrEmpty(password)) return Result.Fail<MailboxSession>(MailServerErrors.PasswordRequired);

        var client = new ImapClient();
        var failed = await connector.LoginAsync(client, MailServerStep.Imap, server.ImapHost, server.ImapPort, server.ImapSecurity, server.Username, password, ct);

        if (failed is not null)
        {
            client.Dispose();
            return Result.Fail<MailboxSession>(failed);
        }

        return Result.Ok<MailboxSession>(new ImapMailboxSession(client, connector));
    }

    public async Task<MailChangePage> ReadChangesAsync(MailboxSession session, string stream, string? cursor, DateTimeOffset since, CancellationToken ct)
    {
        var imap = (ImapMailboxSession)session;
        var folder = await FolderAsync(imap, stream, ct);
        if (folder is null) return MailChangePage.Empty(cursor);

        var validity = folder.UidValidity;
        uint last = 0;

        if (cursor is not null)
        {
            var parts = cursor.Split(':');
            if (parts.Length != 2 || !uint.TryParse(parts[0], out var v) || !uint.TryParse(parts[1], out last)) throw new MailCursorExpiredException();
            if (v != validity) throw new MailCursorExpiredException();
        }

        IList<UniqueId> found = cursor is null
            ? await folder.SearchAsync(SearchQuery.DeliveredAfter(since.UtcDateTime.Date.AddDays(-1)), ct)
            : await folder.SearchAsync(SearchQuery.Uids(new UniqueIdRange(new UniqueId(validity, last + 1), UniqueId.MaxValue)), ct);

        // « n:* » always answers the highest message, even below n: keep only what is new.
        var pending = found.Where(u => u.Id > last).OrderBy(u => u.Id).ToList();

        if (pending.Count == 0)
        {
            // A first read of an empty window still anchors the cursor, so the next run reads only what arrives.
            var anchor = cursor is null ? Math.Max(0, (folder.UidNext?.Id ?? 1) - 1) : last;
            return MailChangePage.Empty($"{validity}:{anchor}");
        }

        var batch = pending.Take(BatchSize).ToList();
        var summaries = await folder.FetchAsync(batch,
            MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId | MessageSummaryItems.Flags
            | MessageSummaryItems.InternalDate | MessageSummaryItems.References, ct);

        var kind = stream == "sent" ? MailFolderKind.Sent : MailFolderKind.Inbox;
        var upserts = summaries
            .Where(s => s.Envelope is not null)
            .Select(s =>
            {
                var envelope = s.Envelope!;
                var externalId = $"{stream}:{validity}:{s.UniqueId.Id}";
                var thread = s.References?.FirstOrDefault() ?? envelope.InReplyTo ?? envelope.MessageId ?? externalId;

                return new MailHeader(
                    externalId, kind, thread, envelope.MessageId,
                    MailAddresses.From(envelope.From).FirstOrDefault(),
                    MailAddresses.From(envelope.To),
                    MailAddresses.From(envelope.Cc),
                    envelope.Date ?? s.InternalDate ?? DateTimeOffset.UtcNow,
                    s.Flags?.HasFlag(MessageFlags.Seen) == true,
                    envelope.Subject,
                    null);
            })
            .ToList();

        return new MailChangePage(upserts, [], $"{validity}:{batch[^1].Id}", HasMore: pending.Count > batch.Count);
    }

    public async Task<MailContent?> ReadContentAsync(MailboxSession session, string externalId, CancellationToken ct)
    {
        var imap = (ImapMailboxSession)session;
        var parts = externalId.Split(':');
        if (parts.Length != 3 || !uint.TryParse(parts[1], out var validity) || !uint.TryParse(parts[2], out var uid)) return null;

        var folder = await FolderAsync(imap, parts[0], ct);
        if (folder is null || folder.UidValidity != validity) return null;

        MimeMessage message;

        try
        {
            message = await folder.GetMessageAsync(new UniqueId(validity, uid), ct);
        }
        catch (MessageNotFoundException)
        {
            return null;
        }

        var attachments = message.Attachments
            .Select((a, i) => new MailAttachmentInfo(
                i.ToString(CultureInfo.InvariantCulture),
                a is MimePart part ? part.FileName ?? "pièce jointe" : "message.eml",
                a.ContentType.MimeType,
                a is MimePart { Content.Stream: { CanSeek: true } stream } ? stream.Length : 0,
                string.Equals(a.ContentDisposition?.Disposition, ContentDisposition.Inline, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return new MailContent(message.HtmlBody ?? message.TextBody, message.HtmlBody is not null, attachments);
    }

    // ── Writing (lot 3) ──────────────────────────────────────────────────

    /// <summary>
    /// Hosts whose SMTP server files a copy in « Envoyés » by itself: appending one more would
    /// show the message twice. Every other server gets the copy appended over IMAP.
    /// </summary>
    private static readonly string[] SelfFilingSmtpHosts = ["gmail.com", "googlemail.com", "office365.com", "outlook.com"];

    public async Task SendAsync(MailboxSession session, ConnectedAccount account, OutgoingMail mail, CancellationToken ct)
    {
        var imap = (ImapMailboxSession)session;
        var server = await db.MailServers.FirstOrDefaultAsync(m => m.AccountId == account.Id, ct);
        var password = await ConnectedAccountSecrets.OutsideTransactionAsync(() =>
            secrets.GetValueAsync(ConnectedAccountSecrets.MailPassword(account.TenantId, account.Id), ct));

        if (server is null || string.IsNullOrEmpty(password)) throw new MailSendException(MailServerErrors.PasswordRequired);

        var message = OutgoingMime.Build(mail);

        using (var smtp = new MailKit.Net.Smtp.SmtpClient())
        {
            var failed = await connector.LoginAsync(smtp, MailServerStep.Smtp, server.SmtpHost, server.SmtpPort, server.SmtpSecurity, server.Username, password, ct);
            if (failed is not null) throw new MailSendException(failed);

            try
            {
                await smtp.SendAsync(message, ct);
            }
            catch (MailKit.Net.Smtp.SmtpCommandException)
            {
                // A recipient, a size or a policy the server refused.
                throw new MailSendException(MailSendException.Refused);
            }
            catch (Exception ex) when (ex is IOException or MailKit.ProtocolException or TimeoutException)
            {
                throw new MailSendException(MailSendException.Failed);
            }
            finally
            {
                await connector.DisconnectQuietlyAsync(smtp);
            }
        }

        if (SelfFilingSmtpHosts.Any(h => server.SmtpHost == h || server.SmtpHost.EndsWith("." + h, StringComparison.Ordinal))) return;

        // Sent: a copy that cannot be filed is not a failure of the sending, which already happened.
        try
        {
            if (await SentFolderAsync(imap.Client, ct) is { } sent) await sent.AppendAsync(message, MessageFlags.Seen, ct);
        }
        catch (Exception ex) when (ex is IOException or MailKit.ProtocolException or MailKit.CommandException or FolderNotFoundException)
        {
            // Logged by the caller's absence of a sent copy; nothing to undo.
        }
    }

    public async Task SetReadAsync(MailboxSession session, string externalId, bool isRead, CancellationToken ct)
    {
        var imap = (ImapMailboxSession)session;
        var parts = externalId.Split(':');
        if (parts.Length != 3 || !uint.TryParse(parts[1], out var validity) || !uint.TryParse(parts[2], out var uid)) return;

        var folder = await FolderAsync(imap, parts[0], ct);
        if (folder is null || folder.UidValidity != validity) return;

        if (folder.Access != FolderAccess.ReadWrite) await folder.OpenAsync(FolderAccess.ReadWrite, ct);

        var id = new UniqueId(validity, uid);
        var request = new StoreFlagsRequest(isRead ? StoreAction.Add : StoreAction.Remove, MessageFlags.Seen) { Silent = true };
        await folder.StoreAsync(id, request, ct);
    }

    public async Task<byte[]?> ReadAttachmentAsync(MailboxSession session, string externalId, string attachmentId, CancellationToken ct)
    {
        var imap = (ImapMailboxSession)session;
        var parts = externalId.Split(':');
        if (parts.Length != 3 || !uint.TryParse(parts[1], out var validity) || !uint.TryParse(parts[2], out var uid)
            || !int.TryParse(attachmentId, NumberStyles.None, CultureInfo.InvariantCulture, out var index)) return null;

        var folder = await FolderAsync(imap, parts[0], ct);
        if (folder is null || folder.UidValidity != validity) return null;

        MimeMessage message;

        try
        {
            message = await folder.GetMessageAsync(new UniqueId(validity, uid), ct);
        }
        catch (MessageNotFoundException)
        {
            return null;
        }

        var entity = message.Attachments.ElementAtOrDefault(index);
        using var stream = new MemoryStream();

        switch (entity)
        {
            case MimePart { Content: { } content }:
                await content.DecodeToAsync(stream, ct);
                break;
            case MessagePart { Message: { } attached }:
                await attached.WriteToAsync(stream, ct);
                break;
            default:
                return null;
        }

        return stream.ToArray();
    }

    private async Task<IMailFolder?> FolderAsync(ImapMailboxSession imap, string stream, CancellationToken ct)
    {
        if (imap.Folders.TryGetValue(stream, out var cached)) return cached;

        IMailFolder? folder = stream == "inbox" ? imap.Client.Inbox : await SentFolderAsync(imap.Client, ct);
        if (folder is not null) await folder.OpenAsync(FolderAccess.ReadOnly, ct);

        imap.Folders[stream] = folder;
        return folder;
    }

    private static async Task<IMailFolder?> SentFolderAsync(ImapClient client, CancellationToken ct)
    {
        if ((client.Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) != 0)
        {
            try
            {
                if (client.GetFolder(SpecialFolder.Sent) is { } special) return special;
            }
            catch (NotSupportedException)
            {
                // Advertised and not delivered: fall back to the names.
            }
        }

        if (client.PersonalNamespaces.Count == 0) return null;

        var root = client.GetFolder(client.PersonalNamespaces[0]);
        var all = await root.GetSubfoldersAsync(true, ct);

        return SentNames
            .Select(name => all.FirstOrDefault(f => string.Equals(f.FullName, name, StringComparison.OrdinalIgnoreCase)
                                                   || string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(f => f is not null);
    }
}

// ── Development ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A simulated Microsoft or Google account (<c>UseFakeProviders</c>) has no real mailbox: it reads
/// as an empty one, so turning its mailbox on does not send a fake token to Graph and mark the
/// account « à reconnecter ». A real mailbox to test with is an IMAP one.
/// </summary>
internal sealed class FakeMailboxReader(IHostEnvironment environment) : IMailboxReader
{
    public bool CanRead(ConnectedAccount account) => environment.IsDevelopment() && FakeConnectedAccountProvider.IsSimulated(account);

    public IReadOnlyList<string> Streams { get; } = ["all"];

    public Task<Result<MailboxSession>> OpenAsync(ConnectedAccount account, CancellationToken ct) =>
        Task.FromResult(Result.Ok<MailboxSession>(new TokenMailboxSession("simulated")));

    public Task<MailChangePage> ReadChangesAsync(MailboxSession session, string stream, string? cursor, DateTimeOffset since, CancellationToken ct) =>
        Task.FromResult(MailChangePage.Empty("simulated"));

    public Task<MailContent?> ReadContentAsync(MailboxSession session, string externalId, CancellationToken ct) =>
        Task.FromResult<MailContent?>(null);

    /// <summary>Refused, not pretended: a simulated account that « sent » would hide that nothing left.</summary>
    public Task SendAsync(MailboxSession session, ConnectedAccount account, OutgoingMail mail, CancellationToken ct) =>
        throw new MailSendException("CONNECTED_MAILBOX_SIMULATED");

    public Task SetReadAsync(MailboxSession session, string externalId, bool isRead, CancellationToken ct) => Task.CompletedTask;

    public Task<byte[]?> ReadAttachmentAsync(MailboxSession session, string externalId, string attachmentId, CancellationToken ct) =>
        Task.FromResult<byte[]?>(null);
}

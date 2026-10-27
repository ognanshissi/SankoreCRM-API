namespace Sankore.Modules.Integration.Tests.Features.ConnectedAccounts;

using System.Net;
using System.Text;
using FluentAssertions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.ConnectedAccounts;
using Xunit;

/// <summary>
/// Connected mailbox, lot 2: the Graph and Gmail readers against answers shaped like the
/// providers' own — paging, deletions, an expired cursor, MIME parts. What they ask is recorded,
/// so a wrong URL fails here rather than against a real mailbox.
/// </summary>
public sealed class MailboxReaderTests
{
    /// <summary>Answers by URL prefix, in order of declaration; records every URL asked.</summary>
    private sealed class Routes : HttpMessageHandler
    {
        private readonly List<(string Prefix, HttpStatusCode Status, string Json)> _routes = [];
        public List<string> Asked { get; } = [];

        public Routes On(string prefix, string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _routes.Add((prefix, status, json));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            Asked.Add(url);
            var route = _routes.FirstOrDefault(r => url.StartsWith(r.Prefix, StringComparison.Ordinal));
            return Task.FromResult(route.Prefix is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
                : new HttpResponseMessage(route.Status) { Content = new StringContent(route.Json, Encoding.UTF8, "application/json") });
        }
    }

    private static readonly DateTimeOffset Since = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    private static readonly MailboxSession Session = new TokenMailboxSession("token");

    // ── Graph ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Graph_reads_a_folder_delta_page_by_page_and_says_what_was_removed()
    {
        var routes = new Routes()
            .On("https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages/delta?", """
                {
                  "value": [
                    { "id": "AAMk1", "conversationId": "conv-1", "internetMessageId": "<a@b>", "subject": "Votre dossier",
                      "bodyPreview": "Bonjour", "isRead": false, "receivedDateTime": "2026-10-26T08:00:00Z",
                      "from": { "emailAddress": { "name": "Aya", "address": "aya@example.ci" } },
                      "toRecipients": [ { "emailAddress": { "address": "awa@cabinet.ci" } } ], "ccRecipients": [] },
                    { "id": "AAMk2", "@removed": { "reason": "deleted" } }
                  ],
                  "@odata.nextLink": "https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages/delta?$skiptoken=X"
                }
                """);
        var reader = new GraphMailboxReader(new HttpClient(routes), null!);

        var page = await reader.ReadChangesAsync(Session, "inbox", null, Since, CancellationToken.None);

        routes.Asked.Single().Should().Contain("$filter=receivedDateTime ge 2026-09-27T00:00:00Z");
        page.HasMore.Should().BeTrue();
        page.Cursor.Should().EndWith("$skiptoken=X");
        page.Removed.Should().Equal("AAMk2");

        var m = page.Upserts.Single();
        m.Should().BeEquivalentTo(new
        {
            ExternalId = "AAMk1", ThreadKey = "conv-1", Folder = MailFolderKind.Inbox, IsRead = false, Subject = "Votre dossier",
            From = new MailParty("aya@example.ci", "Aya"),
        }, o => o.ExcludingMissingMembers());
        m.To.Should().Equal(new MailParty("awa@cabinet.ci", null));
    }

    [Fact]
    public async Task Graph_ends_on_the_delta_link_and_a_forgotten_one_is_an_expired_cursor()
    {
        var done = new Routes().On("https://graph", """{ "value": [], "@odata.deltaLink": "https://graph/delta?$deltatoken=D" }""");
        var page = await new GraphMailboxReader(new HttpClient(done), null!).ReadChangesAsync(Session, "sentitems", "https://graph/next", Since, CancellationToken.None);

        page.HasMore.Should().BeFalse();
        page.Cursor.Should().Be("https://graph/delta?$deltatoken=D");

        var gone = new Routes().On("https://graph", """{ "error": { "code": "SyncStateNotFound" } }""", HttpStatusCode.Gone);
        await new GraphMailboxReader(new HttpClient(gone), null!)
            .Invoking(r => r.ReadChangesAsync(Session, "inbox", "https://graph/delta?$deltatoken=D", Since, CancellationToken.None))
            .Should().ThrowAsync<MailCursorExpiredException>();
    }

    [Fact]
    public async Task Graph_reads_a_kept_message_s_html_body_and_describes_its_attachments()
    {
        var routes = new Routes()
            .On("https://graph.microsoft.com/v1.0/me/messages/AAMk1/attachments", """
                { "value": [ { "id": "att-1", "name": "rib.pdf", "contentType": "application/pdf", "size": 52000, "isInline": false } ] }
                """)
            .On("https://graph.microsoft.com/v1.0/me/messages/AAMk1?", """
                { "hasAttachments": true, "body": { "contentType": "html", "content": "<p>Ci-joint</p>" } }
                """);

        var content = await new GraphMailboxReader(new HttpClient(routes), null!).ReadContentAsync(Session, "AAMk1", CancellationToken.None);

        content!.Body.Should().Be("<p>Ci-joint</p>");
        content.IsHtml.Should().BeTrue();
        content.Attachments.Should().Equal(new MailAttachmentInfo("att-1", "rib.pdf", "application/pdf", 52000, false));
    }

    [Fact]
    public async Task A_refused_Graph_token_asks_for_a_reconnection_and_a_server_error_does_not()
    {
        var refused = new Routes().On("https://graph", "{}", HttpStatusCode.Unauthorized);
        (await new GraphMailboxReader(new HttpClient(refused), null!)
                .Invoking(r => r.ReadChangesAsync(Session, "inbox", null, Since, CancellationToken.None))
                .Should().ThrowAsync<ProviderAuthException>())
            .Which.Revoked.Should().BeTrue();

        var down = new Routes().On("https://graph", "{}", HttpStatusCode.ServiceUnavailable);
        (await new GraphMailboxReader(new HttpClient(down), null!)
                .Invoking(r => r.ReadChangesAsync(Session, "inbox", null, Since, CancellationToken.None))
                .Should().ThrowAsync<ProviderAuthException>())
            .Which.Revoked.Should().BeFalse();
    }

    // ── Gmail ──────────────────────────────────────────────────────────────────

    private const string Api = "https://gmail.googleapis.com/gmail/v1/users/me";

    private static string Metadata(string id, string labels, string from = "Aya <aya@example.ci>") => $$"""
        { "id": "{{id}}", "threadId": "th-1", "labelIds": [{{labels}}], "snippet": "Bonjour l&#39;agence", "internalDate": "1761465600000",
          "payload": { "headers": [
            { "name": "From", "value": "{{from}}" }, { "name": "To", "value": "awa@cabinet.ci" },
            { "name": "Subject", "value": "Votre dossier" }, { "name": "Message-ID", "value": "<{{id}}@mail>" } ] } }
        """;

    [Fact]
    public async Task Gmail_takes_the_history_id_first_lists_the_window_then_hands_over_to_the_history()
    {
        var routes = new Routes()
            .On($"{Api}/profile", """{ "historyId": "500" }""")
            .On($"{Api}/messages?", """{ "messages": [ { "id": "g1" }, { "id": "g2" } ] }""")
            .On($"{Api}/messages/g1?format=metadata", Metadata("g1", "\"INBOX\", \"UNREAD\""))
            .On($"{Api}/messages/g2?format=metadata", Metadata("g2", "\"SPAM\""));

        var page = await new GmailMailboxReader(new HttpClient(routes), null!).ReadChangesAsync(Session, "all", null, Since, CancellationToken.None);

        routes.Asked[1].Should().Contain($"q=after:{Since.ToUnixTimeSeconds()} -in:spam -in:trash");
        page.Cursor.Should().Be("H|500|", "the history id taken BEFORE the listing is where the changes start");
        page.HasMore.Should().BeTrue();

        var m = page.Upserts.Single();
        m.ExternalId.Should().Be("g1", "spam is never read");
        m.IsRead.Should().BeFalse();
        m.Folder.Should().Be(MailFolderKind.Inbox);
        m.ThreadKey.Should().Be("th-1");
        m.From.Should().Be(new MailParty("aya@example.ci", "Aya"));
        m.Snippet.Should().Be("Bonjour l'agence");
        m.SentAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1761465600000));
    }

    [Fact]
    public async Task Gmail_history_reports_additions_label_changes_and_deletions_and_moves_the_cursor()
    {
        var routes = new Routes()
            .On($"{Api}/history?", """
                { "historyId": "640", "history": [
                  { "messagesAdded": [ { "message": { "id": "g3" } } ] },
                  { "labelsRemoved": [ { "message": { "id": "g1" }, "labelIds": ["UNREAD"] } ] },
                  { "messagesDeleted": [ { "message": { "id": "g2" } } ] },
                  { "labelsAdded": [ { "message": { "id": "g4" }, "labelIds": ["TRASH"] } ] } ] }
                """)
            .On($"{Api}/messages/g3?format=metadata", Metadata("g3", "\"SENT\"", from: "awa@cabinet.ci"))
            .On($"{Api}/messages/g1?format=metadata", Metadata("g1", "\"INBOX\""))
            .On($"{Api}/messages/g4?format=metadata", Metadata("g4", "\"TRASH\""));

        var page = await new GmailMailboxReader(new HttpClient(routes), null!).ReadChangesAsync(Session, "all", "H|500|", Since, CancellationToken.None);

        page.Cursor.Should().Be("H|640|");
        page.HasMore.Should().BeFalse();
        page.Removed.Should().BeEquivalentTo(["g2", "g4"], "a message moved to the trash is gone for the CRM");
        page.Upserts.Select(u => (u.ExternalId, u.Folder, u.IsRead)).Should().BeEquivalentTo(new[]
        {
            ("g3", MailFolderKind.Sent, true),
            ("g1", MailFolderKind.Inbox, true),
        });
    }

    [Fact]
    public async Task A_history_id_Gmail_forgot_is_an_expired_cursor()
    {
        var routes = new Routes().On($"{Api}/history?", """{ "error": { "code": 404 } }""", HttpStatusCode.NotFound);
        await new GmailMailboxReader(new HttpClient(routes), null!)
            .Invoking(r => r.ReadChangesAsync(Session, "all", "H|1|", Since, CancellationToken.None))
            .Should().ThrowAsync<MailCursorExpiredException>();
    }

    [Fact]
    public async Task Gmail_content_prefers_the_html_part_decodes_base64url_and_lists_attachments()
    {
        static string B64Url(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var routes = new Routes().On($"{Api}/messages/g1?format=full", $$"""
            { "payload": { "mimeType": "multipart/mixed", "parts": [
                { "mimeType": "multipart/alternative", "parts": [
                    { "mimeType": "text/plain", "body": { "data": "{{B64Url("Bonjour, voici mon RIB ?")}}" } },
                    { "mimeType": "text/html", "body": { "data": "{{B64Url("<p>Bonjour, voici mon RIB ?</p>")}}" } } ] },
                { "mimeType": "application/pdf", "filename": "rib.pdf",
                  "headers": [ { "name": "Content-Disposition", "value": "attachment; filename=rib.pdf" } ],
                  "body": { "attachmentId": "ANGj", "size": 52000 } } ] } }
            """);

        var content = await new GmailMailboxReader(new HttpClient(routes), null!).ReadContentAsync(Session, "g1", CancellationToken.None);

        content!.Body.Should().Be("<p>Bonjour, voici mon RIB ?</p>");
        content.IsHtml.Should().BeTrue();
        content.Attachments.Should().Equal(new MailAttachmentInfo("ANGj", "rib.pdf", "application/pdf", 52000, false));
    }

    [Fact]
    public void Address_headers_are_parsed_by_MimeKit_quoted_names_and_encoded_words_included()
    {
        MailAddresses.Parse("\"Koné, Awa\" <awa@cabinet.ci>, =?utf-8?q?A=C3=AFcha?= <aicha@example.ci>, broken")
            .Should().Equal(new MailParty("awa@cabinet.ci", "Koné, Awa"), new MailParty("aicha@example.ci", "Aïcha"));
    }
}

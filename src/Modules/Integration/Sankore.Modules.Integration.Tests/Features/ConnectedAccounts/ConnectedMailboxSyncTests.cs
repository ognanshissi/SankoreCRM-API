namespace Sankore.Modules.Integration.Tests.Features.ConnectedAccounts;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.ConnectedAccounts;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>Connected mailbox, lot 2: reading a mailbox and keeping only what concerns a lead or a client.</summary>
public sealed class ConnectedMailboxSyncTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _lead = Guid.NewGuid();
    private readonly Guid _client = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private readonly TestIntegrationDbContextFactory _factory;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly ILeadsModule _leads = Substitute.For<ILeadsModule>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly ScriptedReader _reader = new();

    public ConnectedMailboxSyncTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);
        _currentUser.Id.Returns(_me);
        _currentUser.TenantId.Returns(_tenant);

        // aya@ is a lead, the shop's address a client; everybody else is unknown to the CRM.
        _leads.MatchLeadsByEmailsAsync(_tenant, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyCollection<string>>().Where(e => e == "aya@example.ci")
                .Select(e => new LeadEmailMatch(e, _lead)).ToList());
        _customers.MatchClientsByEmailsAsync(_tenant, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyCollection<string>>().Where(e => e == "boutique@example.ci")
                .Select(e => new ClientEmailMatch(e, _client)).ToList());
    }

    public void Dispose() => _factory.Dispose();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Visible « encryption »: stored values are prefixed, so a test can tell ciphertext from clear text.</summary>
    private sealed class PrefixEncryptor : IFieldEncryptor
    {
        public string? Encrypt(string? plaintext) => plaintext is null ? null : "enc:" + plaintext;
        public string? Decrypt(string? ciphertext) => ciphertext is null ? null : ciphertext["enc:".Length..];
    }

    /// <summary>A mailbox whose changes are scripted page by page, per stream.</summary>
    private sealed class ScriptedReader : IMailboxReader
    {
        public Dictionary<string, Queue<Func<string?, MailChangePage>>> Pages { get; } = new();
        public Dictionary<string, MailContent> Contents { get; } = new();
        public List<string> ContentReads { get; } = [];
        public List<(string Stream, string? Cursor)> Reads { get; } = [];
        public string? OpenFailure { get; set; }
        public bool FailContent { get; set; }

        public bool CanRead(ConnectedAccount account) => true;

        public IReadOnlyList<string> Streams { get; } = ["inbox", "sent"];

        public Task<Result<MailboxSession>> OpenAsync(ConnectedAccount account, CancellationToken ct) =>
            Task.FromResult(OpenFailure is null
                ? Result.Ok<MailboxSession>(new TokenMailboxSession("t"))
                : Result.Fail<MailboxSession>(OpenFailure));

        public Task<MailChangePage> ReadChangesAsync(MailboxSession session, string stream, string? cursor, DateTimeOffset since, CancellationToken ct)
        {
            Reads.Add((stream, cursor));
            return Task.FromResult(Pages.TryGetValue(stream, out var q) && q.Count > 0 ? q.Dequeue()(cursor) : MailChangePage.Empty(cursor));
        }

        public Task<MailContent?> ReadContentAsync(MailboxSession session, string externalId, CancellationToken ct)
        {
            ContentReads.Add(externalId);
            if (FailContent) throw new ProviderAuthException("graph_http_503", revoked: false);
            return Task.FromResult<MailContent?>(Contents.GetValueOrDefault(externalId) ?? new MailContent("<p>Bonjour</p>", true, []));
        }

        public Task SendAsync(MailboxSession session, ConnectedAccount account, OutgoingMail mail, CancellationToken ct) => throw new NotSupportedException();
        public Task SetReadAsync(MailboxSession session, string externalId, bool isRead, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]?> ReadAttachmentAsync(MailboxSession session, string externalId, string attachmentId, CancellationToken ct) => throw new NotSupportedException();

        public void Script(string stream, params Func<string?, MailChangePage>[] pages)
        {
            if (!Pages.TryGetValue(stream, out var q)) Pages[stream] = q = new Queue<Func<string?, MailChangePage>>();
            foreach (var p in pages) q.Enqueue(p);
        }
    }

    private static MailHeader Header(
        string id, string from, string to, MailFolderKind folder = MailFolderKind.Inbox, bool isRead = false, string thread = "t1",
        string? subject = "Votre dossier", int minutesAgo = 10) =>
        new(id, folder, thread, $"<{id}@mail>", new MailParty(from, null), [new MailParty(to, null)], [],
            new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero).AddMinutes(-minutesAgo), isRead, subject, "Bonjour Awa");

    // ── Keeping ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Only_e_mails_exchanged_with_a_lead_or_a_client_are_kept_and_only_they_are_fetched()
    {
        var account = await AccountAsync();
        _reader.Script("inbox", _ => new MailChangePage(
        [
            Header("m1", "aya@example.ci", "awa@cabinet.ci"),
            Header("m2", "newsletter@promo.example", "awa@cabinet.ci"),
            Header("m3", "collegue@cabinet.ci", "boutique@example.ci", thread: "t2"),
        ], [], "inbox-1", HasMore: false));

        var result = await SyncAsync(account);

        result.Value!.Kept.Should().Be(2);
        _reader.ContentReads.Should().BeEquivalentTo(["m1", "m3"], "a message nobody is matched to is never fetched in full");

        await using var db = _factory.CreateContext();
        (await db.ConnectedMailMessages.Select(m => m.ExternalId).ToListAsync()).Should().BeEquivalentTo(["m1", "m3"]);

        var links = await db.ConnectedMailLinks.ToListAsync();
        links.Should().BeEquivalentTo(new[]
        {
            new { EntityType = MailLinkEntity.Lead, EntityId = _lead, UserId = _me, Direction = MailDirection.Received },
            new { EntityType = MailLinkEntity.Client, EntityId = _client, UserId = _me, Direction = MailDirection.Received },
        }, o => o.ExcludingMissingMembers());
    }

    [Fact]
    public async Task What_a_person_wrote_or_is_named_by_is_stored_encrypted()
    {
        var account = await AccountAsync();
        _reader.Script("inbox", _ => new MailChangePage([Header("m1", "aya@example.ci", "awa@cabinet.ci")], [], "c", false));
        _reader.Contents["m1"] = new MailContent("<p>Mon RIB</p>", true, [new MailAttachmentInfo("a1", "rib.pdf", "application/pdf", 1200, false)]);

        await SyncAsync(account);

        await using var db = _factory.CreateContext();
        var stored = await db.ConnectedMailMessages.SingleAsync();
        stored.SubjectEnc.Should().Be("enc:Votre dossier");
        stored.BodyEnc.Should().Be("enc:<p>Mon RIB</p>");
        stored.FromEnc.Should().StartWith("enc:").And.Contain("aya@example.ci");
        stored.AttachmentsEnc.Should().StartWith("enc:").And.Contain("rib.pdf");
        stored.HasAttachments.Should().BeTrue();
    }

    [Fact]
    public async Task A_sent_message_is_sent_and_read_and_the_mailbox_s_own_address_matches_nobody()
    {
        var account = await AccountAsync();
        _reader.Script("sent", _ => new MailChangePage(
            [Header("s1", "awa@cabinet.ci", "aya@example.ci", MailFolderKind.Sent, isRead: false)], [], "sent-1", false));

        await SyncAsync(account);

        await using var db = _factory.CreateContext();
        var stored = await db.ConnectedMailMessages.SingleAsync();
        stored.Direction.Should().Be(MailDirection.Sent);
        stored.IsRead.Should().BeTrue();

        await _leads.Received().MatchLeadsByEmailsAsync(_tenant,
            Arg.Is<IReadOnlyCollection<string>>(a => !a.Contains("awa@cabinet.ci")), Arg.Any<CancellationToken>());
    }

    // ── Cursors ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pages_are_read_until_the_last_and_the_next_run_starts_from_the_saved_cursor()
    {
        var account = await AccountAsync();
        _reader.Script("inbox",
            _ => new MailChangePage([Header("m1", "aya@example.ci", "awa@cabinet.ci")], [], "page-2", HasMore: true),
            _ => new MailChangePage([Header("m2", "aya@example.ci", "awa@cabinet.ci")], [], "delta-1", HasMore: false));

        await SyncAsync(account);
        await SyncAsync(account);

        _reader.Reads.Where(r => r.Stream == "inbox").Select(r => r.Cursor).Should().Equal(null, "page-2", "delta-1");

        await using var db = _factory.CreateContext();
        (await db.MailboxSyncStates.SingleAsync(s => s.Stream == "inbox")).Cursor.Should().Be("delta-1");
        (await db.ConnectedAccounts.SingleAsync()).MailSyncedAt.Should().Be(_now);
    }

    [Fact]
    public async Task A_kept_message_is_refreshed_without_a_second_fetch_and_a_removed_one_goes_with_its_links()
    {
        var account = await AccountAsync();
        _reader.Script("inbox",
            _ => new MailChangePage(
                [Header("m1", "aya@example.ci", "awa@cabinet.ci"), Header("m2", "boutique@example.ci", "awa@cabinet.ci", thread: "t2")],
                [], "d1", false),
            _ => new MailChangePage([Header("m1", "aya@example.ci", "awa@cabinet.ci", isRead: true)], ["m2"], "d2", false));

        await SyncAsync(account);
        await SyncAsync(account);

        _reader.ContentReads.Should().BeEquivalentTo(["m1", "m2"]);

        await using var db = _factory.CreateContext();
        (await db.ConnectedMailMessages.SingleAsync()).IsRead.Should().BeTrue();
        (await db.ConnectedMailLinks.SingleAsync()).EntityType.Should().Be(MailLinkEntity.Lead);
    }

    [Fact]
    public async Task An_expired_cursor_reads_the_window_again_without_duplicating_what_was_kept()
    {
        var account = await AccountAsync();
        _reader.Script("inbox",
            _ => new MailChangePage([Header("m1", "aya@example.ci", "awa@cabinet.ci")], [], "d1", false),
            _ => throw new MailCursorExpiredException(),
            cursor => cursor is null
                ? new MailChangePage([Header("m1", "aya@example.ci", "awa@cabinet.ci")], [], "d-new", false)
                : throw new InvalidOperationException("the window must be read from scratch"));

        await SyncAsync(account);
        await SyncAsync(account);

        await using var db = _factory.CreateContext();
        (await db.ConnectedMailMessages.CountAsync()).Should().Be(1);
        (await db.MailboxSyncStates.SingleAsync(s => s.Stream == "inbox")).Cursor.Should().Be("d-new");
        _reader.ContentReads.Should().ContainSingle();
    }

    [Fact]
    public async Task A_content_that_cannot_be_fetched_fails_the_page_and_keeps_the_cursor_so_nothing_is_lost()
    {
        var account = await AccountAsync();
        _reader.FailContent = true;
        _reader.Script("inbox", _ => new MailChangePage([Header("m1", "aya@example.ci", "awa@cabinet.ci")], [], "d1", false));

        var result = await SyncAsync(account);

        result.Value!.Failures.Should().ContainSingle();
        await using var db = _factory.CreateContext();
        (await db.ConnectedMailMessages.CountAsync()).Should().Be(0);
        var state = await db.MailboxSyncStates.SingleAsync(s => s.Stream == "inbox");
        state.Cursor.Should().BeNull("the page is read again on the next run");
        state.LastError.Should().Be("graph_http_503");
        (await db.ConnectedAccounts.SingleAsync()).MailLastError.Should().Be("graph_http_503");
    }

    [Fact]
    public async Task A_refused_IMAP_password_asks_the_user_to_reconnect_and_a_mailbox_turned_off_is_not_read()
    {
        var account = await AccountAsync();
        _reader.OpenFailure = "MAIL_IMAP_AUTH_FAILED";

        (await SyncAsync(account)).Error.Should().Be("MAIL_IMAP_AUTH_FAILED");

        await using (var db = _factory.CreateContext())
            (await db.ConnectedAccounts.SingleAsync()).Status.Should().Be(ConnectedAccountStatus.NeedsReconnect);

        var oauth = await AccountAsync(ConnectedAccountProvider.Google, mail: false);
        (await SyncAsync(oauth)).Value!.Kept.Should().Be(0);
        _reader.Reads.Should().BeEmpty();
    }

    // ── Reading back ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_owner_reads_their_conversations_and_a_colleague_reads_nothing()
    {
        var account = await AccountAsync();
        _reader.Script("inbox", _ => new MailChangePage(
        [
            Header("m1", "aya@example.ci", "awa@cabinet.ci", minutesAgo: 30),
            Header("m2", "aya@example.ci", "awa@cabinet.ci", minutesAgo: 5, subject: "RE: Votre dossier"),
            Header("m3", "boutique@example.ci", "awa@cabinet.ci", thread: "t2", minutesAgo: 20, isRead: true),
        ], [], "d1", false));
        await SyncAsync(account);

        await using var db = _factory.CreateContext();
        var page = await new ListMyMailThreadsHandler(db, _currentUser, Sync(db))
            .Handle(new ListMyMailThreadsQuery(null, false, 1, 25), CancellationToken.None);

        page.TotalCount.Should().Be(2);
        var first = page.Items[0];
        first.Subject.Should().Be("RE: Votre dossier");
        first.MessageCount.Should().Be(2);
        first.UnreadCount.Should().Be(2);
        first.From!.Address.Should().Be("aya@example.ci");
        first.Links.Should().ContainSingle().Which.Should().Be(new MailLinkDto("Lead", _lead));

        // With the names resolved (lot 5): the screen shows who, never an identifier.
        _leads.GetLeadAsync(_lead, Arg.Any<CancellationToken>()).Returns(new LeadSummary(_lead, "Aya Kouassi", "Open", "Agency", null));
        var named = await new ListMyMailThreadsHandler(db, _currentUser, Sync(db), new MailLinkNames(_currentUser, _leads, _customers))
            .Handle(new ListMyMailThreadsQuery(null, false, 1, 25), CancellationToken.None);
        named.Items[0].Links.Should().ContainSingle().Which.Name.Should().Be("Aya Kouassi");

        (await new ListMyMailThreadsHandler(db, _currentUser, Sync(db)).Handle(new ListMyMailThreadsQuery(null, true, 1, 25), CancellationToken.None))
            .TotalCount.Should().Be(1, "the shop's conversation is read");

        var thread = await new GetMyMailThreadHandler(db, _currentUser, Sync(db)).Handle(new GetMyMailThreadQuery(first.LatestMessageId), CancellationToken.None);
        thread.Value!.Messages.Select(m => m.Subject).Should().Equal("Votre dossier", "RE: Votre dossier");
        thread.Value.Messages[0].Body.Should().Be("<p>Bonjour</p>");

        var colleague = Substitute.For<ICurrentUser>();
        colleague.Id.Returns(Guid.NewGuid());
        colleague.TenantId.Returns(_tenant);
        (await new ListMyMailThreadsHandler(db, colleague, Sync(db)).Handle(new ListMyMailThreadsQuery(null, false, 1, 25), CancellationToken.None))
            .TotalCount.Should().Be(0);
        (await new GetMyMailThreadHandler(db, colleague, Sync(db)).Handle(new GetMyMailThreadQuery(first.LatestMessageId), CancellationToken.None))
            .Error.Should().Be(GetMyMailThreadHandler.NotFound);
    }

    // ── Forgetting ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Turning_the_mailbox_off_deletes_what_was_kept_from_it()
    {
        var account = await AccountAsync(ConnectedAccountProvider.Google);
        _reader.Script("inbox", _ => new MailChangePage([Header("m1", "aya@example.ci", "awa@cabinet.ci")], [], "d1", false));
        await SyncAsync(account);

        await using var db = _factory.CreateContext();
        (await new DisableMailboxHandler(db, _currentUser).Handle(new DisableMailboxCommand(account.Id), CancellationToken.None)).IsSuccess.Should().BeTrue();

        (await db.ConnectedMailMessages.CountAsync()).Should().Be(0);
        (await db.ConnectedMailLinks.CountAsync()).Should().Be(0);
        (await db.MailboxSyncStates.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Erasing_a_lead_deletes_every_e_mail_kept_because_of_it_and_nothing_else()
    {
        var account = await AccountAsync();
        _reader.Script("inbox", _ => new MailChangePage(
        [
            Header("m1", "aya@example.ci", "awa@cabinet.ci"),
            Header("m2", "boutique@example.ci", "awa@cabinet.ci", thread: "t2"),
        ], [], "d1", false));
        await SyncAsync(account);

        var services = new ServiceCollection().AddScoped(_ => _factory.CreateContext()).BuildServiceProvider();
        await ConnectedMailErasure.EraseAsync(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance,
            _tenant, MailLinkEntity.Lead, _lead, CancellationToken.None);

        await using var db = _factory.CreateContext();
        (await db.ConnectedMailMessages.SingleAsync()).ExternalId.Should().Be("m2");
        (await db.ConnectedMailLinks.SingleAsync()).EntityId.Should().Be(_client);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private ConnectedMailboxSync Sync(IntegrationDbContext db) => new(
        db, [_reader], new CalendarEventProtector(new PrefixEncryptor()), new FixedClock(_now),
        NullLogger<ConnectedMailboxSync>.Instance, _leads, _customers);

    private async Task<Result<MailSyncOutcome>> SyncAsync(ConnectedAccount account)
    {
        await using var db = _factory.CreateContext();
        var tracked = await db.ConnectedAccounts.AsTracking().SingleAsync(a => a.Id == account.Id);
        return await Sync(db).SyncAccountAsync(tracked, CancellationToken.None);
    }

    private async Task<ConnectedAccount> AccountAsync(ConnectedAccountProvider provider = ConnectedAccountProvider.Imap, bool mail = true)
    {
        var account = provider == ConnectedAccountProvider.Imap
            ? ConnectedAccount.ConnectImap(_tenant, _me, "awa@cabinet.ci", null, null, "awa@cabinet.ci", "mail.cabinet.ci", _now)
            : ConnectedAccount.Connect(_tenant, _me, provider, $"ext-{Guid.NewGuid():N}", "awa@cabinet.ci", null, null, "openid", _now);

        if (provider != ConnectedAccountProvider.Imap && mail) account.ApplyMailGrant(true, _now);

        await using var db = _factory.CreateContext();
        db.ConnectedAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }
}

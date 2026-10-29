namespace Sankore.Modules.Integration.Tests.Features.ConnectedAccounts;

using System.Net;
using System.Text;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.ConnectedAccounts;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>Connected mailbox, lot 3: sending, read flags, attachments and templates.</summary>
public sealed class ConnectedMailboxSendTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private readonly TestIntegrationDbContextFactory _factory;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();
    private readonly WritingMailbox _mailbox = new();

    public ConnectedMailboxSendTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);
        _currentUser.Id.Returns(_me);
        _currentUser.TenantId.Returns(_tenant);
        _currentUser.DisplayName.Returns("Awa Koné");
    }

    public void Dispose() => _factory.Dispose();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class PrefixEncryptor : IFieldEncryptor
    {
        public string? Encrypt(string? plaintext) => plaintext is null ? null : "enc:" + plaintext;
        public string? Decrypt(string? ciphertext) => ciphertext is null ? null : ciphertext["enc:".Length..];
    }

    /// <summary>A mailbox that records what it was asked to write.</summary>
    private sealed class WritingMailbox : IMailboxReader
    {
        public List<OutgoingMail> Sent { get; } = [];
        public List<(string Id, bool IsRead)> Flags { get; } = [];
        public Dictionary<string, byte[]> Attachments { get; } = new();
        public HashSet<string> RefuseFlag { get; } = [];
        public Exception? SendFailure { get; set; }
        public string? OpenFailure { get; set; }

        public bool CanRead(ConnectedAccount account) => true;
        public IReadOnlyList<string> Streams { get; } = ["inbox"];

        public Task<Result<MailboxSession>> OpenAsync(ConnectedAccount account, CancellationToken ct) =>
            Task.FromResult(OpenFailure is null ? Result.Ok<MailboxSession>(new TokenMailboxSession("t")) : Result.Fail<MailboxSession>(OpenFailure));

        public Task<MailChangePage> ReadChangesAsync(MailboxSession session, string stream, string? cursor, DateTimeOffset since, CancellationToken ct) =>
            Task.FromResult(MailChangePage.Empty(cursor));

        public Task<MailContent?> ReadContentAsync(MailboxSession session, string externalId, CancellationToken ct) => Task.FromResult<MailContent?>(null);

        public Task SendAsync(MailboxSession session, ConnectedAccount account, OutgoingMail mail, CancellationToken ct)
        {
            if (SendFailure is not null) throw SendFailure;
            Sent.Add(mail);
            return Task.CompletedTask;
        }

        public Task SetReadAsync(MailboxSession session, string externalId, bool isRead, CancellationToken ct)
        {
            if (RefuseFlag.Contains(externalId)) throw new ProviderAuthException("graph_http_503", revoked: false);
            Flags.Add((externalId, isRead));
            return Task.CompletedTask;
        }

        public Task<byte[]?> ReadAttachmentAsync(MailboxSession session, string externalId, string attachmentId, CancellationToken ct) =>
            Task.FromResult(Attachments.GetValueOrDefault($"{externalId}/{attachmentId}"));
    }

    // ── Sending ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_new_message_goes_from_the_mailbox_to_every_recipient_and_a_sync_follows()
    {
        var account = await AccountAsync();

        var result = await SendAsync(Command(account.Id, to: ["aya@example.ci, koffi@example.ci", "aya@example.ci"], cc: ["boutique@example.ci"]));

        result.IsSuccess.Should().BeTrue(result.Error);
        var mail = _mailbox.Sent.Single();
        mail.From.Should().Be(new MailParty("awa@cabinet.ci", null));
        mail.To.Select(p => p.Address).Should().Equal("aya@example.ci", "koffi@example.ci");
        mail.Cc.Select(p => p.Address).Should().Equal("boutique@example.ci");
        mail.ReplyTo.Should().BeNull();
        _jobs.ReceivedWithAnyArgs(1).Create(default!, default!);
    }

    [Theory]
    [InlineData("pas-une-adresse", MailSendErrors.RecipientInvalid)]
    [InlineData("Aya <aya@example.ci>", MailSendErrors.RecipientInvalid)]
    [InlineData("", MailSendErrors.RecipientsRequired)]
    public async Task Recipients_are_bare_valid_addresses(string to, string code)
    {
        var account = await AccountAsync();
        (await SendAsync(Command(account.Id, to: [to]))).Error.Should().Be(code);
        _mailbox.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Executables_too_many_files_and_too_many_bytes_are_refused_before_anything_leaves()
    {
        var account = await AccountAsync();

        (await SendAsync(Command(account.Id, attachments: [new OutgoingAttachment("facture.exe", "application/octet-stream", [1])])))
            .Error.Should().Be(MailSendErrors.AttachmentBlocked);
        (await SendAsync(Command(account.Id, attachments: [.. Enumerable.Range(0, 11).Select(i => new OutgoingAttachment($"p{i}.pdf", "application/pdf", [1]))])))
            .Error.Should().Be(MailSendErrors.TooManyAttachments);
        (await SendAsync(Command(account.Id, attachments: [new OutgoingAttachment("scan.pdf", "application/pdf", new byte[MailSendLimits.MaxAttachmentBytes + 1])])))
            .Error.Should().Be(MailSendErrors.AttachmentsTooLarge);

        _mailbox.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_reply_is_threaded_on_the_answered_message_and_only_from_its_own_mailbox()
    {
        var account = await AccountAsync();
        var other = await AccountAsync(email: "perso@gmail.test");
        var answered = await MessageAsync(account, "m1");

        (await SendAsync(Command(account.Id, replyTo: answered.Id))).IsSuccess.Should().BeTrue();
        _mailbox.Sent.Single().ReplyTo.Should().Be(new OutgoingReplyTo("m1", "<m1@mail>", "thread-1"));

        (await SendAsync(Command(other.Id, replyTo: answered.Id))).Error.Should().Be(MailSendErrors.OtherMailbox);

        var colleague = Substitute.For<ICurrentUser>();
        colleague.Id.Returns(Guid.NewGuid());
        colleague.TenantId.Returns(_tenant);
        (await SendAsync(Command(account.Id, replyTo: answered.Id), colleague)).Error.Should().Be(ConnectedAccountErrors.NotFound);
    }

    [Fact]
    public async Task A_forward_carries_the_chosen_attachments_of_the_original_fetched_at_send_time()
    {
        var account = await AccountAsync();
        var original = await MessageAsync(account, "m1", [new MailAttachmentInfo("att-1", "rib.pdf", "application/pdf", 3, false)]);
        _mailbox.Attachments["m1/att-1"] = [1, 2, 3];

        (await SendAsync(Command(account.Id, forward: original.Id, forwardAttachments: ["att-1"]))).IsSuccess.Should().BeTrue();

        _mailbox.Sent.Single().Attachments.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new OutgoingAttachment("rib.pdf", "application/pdf", [1, 2, 3]));

        (await SendAsync(Command(account.Id, forward: original.Id, forwardAttachments: ["inconnue"]))).Error.Should().Be(MailSendErrors.AttachmentNotFound);
    }

    [Fact]
    public async Task A_refused_password_or_token_asks_for_a_reconnection_and_a_refusal_is_reported_as_such()
    {
        var account = await AccountAsync();

        _mailbox.SendFailure = new MailSendException(MailSendException.Refused);
        (await SendAsync(Command(account.Id))).Error.Should().Be(MailSendException.Refused);
        await StatusShouldBe(account.Id, ConnectedAccountStatus.Active);

        _mailbox.SendFailure = new MailSendException("MAIL_SMTP_AUTH_FAILED");
        (await SendAsync(Command(account.Id))).Error.Should().Be("MAIL_SMTP_AUTH_FAILED");
        await StatusShouldBe(account.Id, ConnectedAccountStatus.NeedsReconnect);

        (await SendAsync(Command(account.Id))).Error.Should().Be(ConnectedAccountErrors.NeedsReconnect, "nothing is attempted until it is reconnected");
        _jobs.ReceivedWithAnyArgs(0).Create(default!, default!);
    }

    [Fact]
    public async Task A_client_named_by_record_is_addressed_by_M01_for_the_caller_and_never_twice()
    {
        var account = await AccountAsync();
        var client = Guid.NewGuid();
        var customers = Substitute.For<ICustomersModule>();
        customers.ResolveEmailForMailAsync(_tenant, client, _me, Arg.Any<CancellationToken>())
            .Returns(Result.Ok("Aya@Example.ci"));

        var result = await SendAsync(Command(account.Id, to: ["aya@example.ci", "koffi@example.ci"]) with { ToClientId = client }, customers: customers);
        result.IsSuccess.Should().BeTrue(result.Error);
        _mailbox.Sent.Single().To.Select(p => p.Address).Should().Equal("aya@example.ci", "koffi@example.ci");

        // Alone in « À » : the address the agent never saw is the only recipient.
        _mailbox.Sent.Clear();
        (await SendAsync(Command(account.Id, to: []) with { ToClientId = client }, customers: customers)).IsSuccess.Should().BeTrue();
        _mailbox.Sent.Single().To.Select(p => p.Address).Should().Equal("Aya@Example.ci");
    }

    [Theory]
    [InlineData("CLIENT_NOT_FOUND", MailSendErrors.ClientNotReachable)]
    [InlineData("CLIENT_PROCESSING_RESTRICTED", MailSendErrors.ClientRestricted)]
    [InlineData("CLIENT_EMAIL_MISSING", MailSendErrors.ClientEmailMissing)]
    public async Task A_client_M01_refuses_sends_nothing_to_anybody(string refusal, string code)
    {
        var account = await AccountAsync();
        var customers = Substitute.For<ICustomersModule>();
        customers.ResolveEmailForMailAsync(default, default, default, default).ReturnsForAnyArgs(Result.Fail<string>(refusal));

        var result = await SendAsync(Command(account.Id, to: ["koffi@example.ci"]) with { ToClientId = Guid.NewGuid() }, customers: customers);

        result.Error.Should().Be(code);
        _mailbox.Sent.Should().BeEmpty();
    }

    // ── Scheduled send ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_scheduled_message_is_checked_now_kept_encrypted_with_its_files_and_handed_to_hangfire()
    {
        var account = await AccountAsync();
        var client = Guid.NewGuid();
        var customers = Substitute.For<ICustomersModule>();
        customers.ResolveEmailForMailAsync(_tenant, client, _me, Arg.Any<CancellationToken>()).Returns(Result.Ok("client@example.ci"));

        var result = await SendAsync(Command(account.Id, to: ["aya@example.ci"], subject: "Prêt 2 000 000",
                attachments: [new OutgoingAttachment("rib.pdf", "application/pdf", [1, 2, 3])]) with { ToClientId = client, SendAt = _now.AddHours(10) },
            customers: customers);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.SendAt.Should().Be(_now.AddHours(10));
        _mailbox.Sent.Should().BeEmpty("nothing leaves before its time");
        _jobs.Received(1).Create(Arg.Is<Job>(j => j.Type == typeof(SendScheduledMailJob)), Arg.Any<ScheduledState>());

        await using var db = _factory.CreateContext();
        var stored = await db.ScheduledMails.SingleAsync();
        stored.Status.Should().Be(ScheduledMailStatus.Pending);
        stored.PayloadEnc.Should().StartWith("enc:", "recipients, subject and body are encrypted");
        var payload = System.Text.Json.JsonSerializer.Deserialize<ScheduledMailPayload>(stored.PayloadEnc["enc:".Length..])!;
        payload.To.Should().Equal(["client@example.ci", "aya@example.ci"], "the client was addressed at scheduling time, with the agent's rights");
        (await db.ScheduledMailAttachments.SingleAsync()).ContentEnc.Should().Be("enc:" + Convert.ToBase64String([1, 2, 3]));

        var listed = await new ListMyScheduledMailsHandler(db, _currentUser, new CalendarEventProtector(new PrefixEncryptor()))
            .Handle(new ListMyScheduledMailsQuery(), CancellationToken.None);
        listed.Should().ContainSingle().Which.Should().Match<ScheduledMailDto>(m => m.Subject == "Prêt 2 000 000" && m.AttachmentCount == 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(400 * 24 * 60)]
    public async Task A_time_under_a_minute_or_over_a_year_away_is_refused_before_anything_is_kept(int minutes)
    {
        var account = await AccountAsync();
        (await SendAsync(Command(account.Id) with { SendAt = _now.AddMinutes(minutes) })).Error.Should().Be(MailSendErrors.ScheduleInvalid);

        await using var db = _factory.CreateContext();
        (await db.ScheduledMails.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_scheduled_message_is_cancelled_by_its_owner_only_and_a_failed_one_retried_now()
    {
        var account = await AccountAsync();
        var scheduled = (await SendAsync(Command(account.Id, attachments: [new OutgoingAttachment("a.pdf", "application/pdf", [1])]) with { SendAt = _now.AddDays(1) })).Value!.ScheduledId!.Value;

        var colleague = Substitute.For<ICurrentUser>();
        colleague.Id.Returns(Guid.NewGuid());
        await using (var db = _factory.CreateContext())
        {
            (await new CancelScheduledMailHandler(db, colleague).Handle(new CancelScheduledMailCommand(scheduled), CancellationToken.None))
                .Error.Should().Be(ScheduledMails.NotFound);
            (await new CancelScheduledMailHandler(db, _currentUser).Handle(new CancelScheduledMailCommand(scheduled), CancellationToken.None))
                .IsSuccess.Should().BeTrue();
        }

        await using (var db = _factory.CreateContext())
        {
            (await db.ScheduledMails.CountAsync()).Should().Be(0);
            (await db.ScheduledMailAttachments.CountAsync()).Should().Be(0, "the files go with the message");
        }

        // A message that failed at its time goes again now, through Hangfire.
        var failed = ScheduledMail.Create(_tenant, _me, account.Id, _now.AddHours(1), "enc:{}", _now).Value!;
        failed.TryStartSending().Should().BeTrue();
        failed.Fail("CONNECTED_ACCOUNT_NEEDS_RECONNECT", _now);
        await using (var db = _factory.CreateContext())
        {
            db.ScheduledMails.Add(failed);
            await db.SaveChangesAsync();
            (await new RetryScheduledMailHandler(db, _currentUser, new FixedClock(_now.AddHours(2)), _jobs).Handle(new RetryScheduledMailCommand(failed.Id), CancellationToken.None))
                .IsSuccess.Should().BeTrue();
        }

        await using (var db = _factory.CreateContext())
        {
            var retried = await db.ScheduledMails.SingleAsync();
            retried.Status.Should().Be(ScheduledMailStatus.Pending);
            retried.SendAt.Should().Be(_now.AddHours(2));
            retried.LastError.Should().BeNull();
        }
    }

    [Fact]
    public void A_scheduled_message_is_taken_once_and_one_being_sent_is_no_longer_cancellable_or_retried()
    {
        var mail = ScheduledMail.Create(_tenant, _me, Guid.NewGuid(), _now.AddHours(1), "enc:{}", _now).Value!;

        mail.TryStartSending().Should().BeTrue();
        mail.TryStartSending().Should().BeFalse("a second run of the job sends nothing");
        mail.Retry(_now).Error.Should().Be("MAIL_SCHEDULE_NOT_FAILED");
    }

    [Fact]
    public async Task Turning_a_mailbox_off_deletes_what_was_waiting_to_leave_from_it()
    {
        var account = await AccountAsync();
        await SendAsync(Command(account.Id) with { SendAt = _now.AddDays(1) });

        await using var db = _factory.CreateContext();
        await ConnectedMailboxSync.ForgetMailboxAsync(db, account.Id, CancellationToken.None);
        await db.SaveChangesAsync();
        (await db.ScheduledMails.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task At_its_time_the_message_leaves_as_its_owner_with_its_files_and_the_row_is_gone()
    {
        var account = await AccountAsync();
        var id = (await SendAsync(Command(account.Id, to: ["aya@example.ci"], attachments: [new OutgoingAttachment("rib.pdf", "application/pdf", [4, 5])])
            with { SendAt = _now.AddHours(1) })).Value!.ScheduledId!.Value;

        var sender = Substitute.For<MediatR.ISender>();
        sender.Send(Arg.Any<SendMailCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Ok(new SendMailOutcome(null, null)));
        await new SendScheduledMailJob(JobServices(sender, Substitute.For<Sankore.Modules.Notifications.PublicApi.INotificationsModule>()), NullLogger<SendScheduledMailJob>.Instance)
            .ExecuteAsync(_tenant, id);

        await sender.Received(1).Send(Arg.Is<SendMailCommand>(c =>
            c.AccountId == account.Id && c.To.SequenceEqual(new[] { "aya@example.ci" }) && c.SendAt == null
            && c.Attachments.Single().Content.SequenceEqual(new byte[] { 4, 5 })), Arg.Any<CancellationToken>());

        await using var db = _factory.CreateContext();
        (await db.ScheduledMails.CountAsync()).Should().Be(0, "the copy that stays is the one in « Envoyés »");
        (await db.ScheduledMailAttachments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_message_that_cannot_leave_is_kept_as_failed_and_its_owner_is_told_once()
    {
        var account = await AccountAsync();
        var id = (await SendAsync(Command(account.Id) with { SendAt = _now.AddHours(1) })).Value!.ScheduledId!.Value;

        var sender = Substitute.For<MediatR.ISender>();
        sender.Send(Arg.Any<SendMailCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail<SendMailOutcome>("CONNECTED_ACCOUNT_NEEDS_RECONNECT"));
        var notifications = Substitute.For<Sankore.Modules.Notifications.PublicApi.INotificationsModule>();
        var job = new SendScheduledMailJob(JobServices(sender, notifications), NullLogger<SendScheduledMailJob>.Instance);

        await job.ExecuteAsync(_tenant, id);
        await job.ExecuteAsync(_tenant, id);

        await sender.Received(1).Send(Arg.Any<SendMailCommand>(), Arg.Any<CancellationToken>());
        await notifications.Received(1).NotifyInAppAsync(
            Arg.Is<Sankore.Modules.Notifications.PublicApi.InAppNotificationRequest>(n => n.UserId == _me && n.Link == "/tasks/mail?programmes=1"),
            Arg.Any<CancellationToken>());

        await using var db = _factory.CreateContext();
        var failed = await db.ScheduledMails.SingleAsync();
        failed.Status.Should().Be(ScheduledMailStatus.Failed);
        failed.LastError.Should().Be("CONNECTED_ACCOUNT_NEEDS_RECONNECT");
    }

    private IServiceScopeFactory JobServices(MediatR.ISender sender, Sankore.Modules.Notifications.PublicApi.INotificationsModule notifications) =>
        new ServiceCollection()
            .AddScoped(_ => _factory.CreateContext())
            .AddSingleton(new CalendarEventProtector(new PrefixEncryptor()))
            .AddSingleton<TimeProvider>(new FixedClock(_now.AddHours(1)))
            .AddSingleton(sender)
            .AddSingleton(notifications)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    // ── Signature ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_signature_is_kept_without_anything_that_runs_and_an_empty_one_is_none()
    {
        var account = await AccountAsync();
        await using var db = _factory.CreateContext();
        var handler = new SetMailSignatureHandler(db, _currentUser);

        (await handler.Handle(new SetMailSignatureCommand(account.Id,
            "<p onclick=\"steal()\">Awa Koné<script>alert(1)</script></p><p><a href=\"javascript:alert(1)\">site</a> <img src=\"data:image/png;base64,AAA\"></p>"),
            CancellationToken.None)).IsSuccess.Should().BeTrue();

        var signature = (await db.ConnectedAccounts.SingleAsync()).MailSignature!;
        signature.Should().Contain("Awa Koné").And.Contain("data:image/png").And.NotContain("script").And.NotContain("onclick").And.NotContain("javascript:");

        (await handler.Handle(new SetMailSignatureCommand(account.Id, "<p><br></p>"), CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await db.ConnectedAccounts.SingleAsync()).MailSignature.Should().BeNull();

        (await handler.Handle(new SetMailSignatureCommand(account.Id, "<p>" + new string('x', MailSignatures.MaxLength) + "</p>"), CancellationToken.None))
            .Error.Should().Be("MAIL_SIGNATURE_TOO_LONG");
    }

    [Fact]
    public async Task A_mailbox_turned_off_sends_nothing()
    {
        var account = await AccountAsync(mail: false);
        (await SendAsync(Command(account.Id))).Error.Should().Be(MailSendErrors.MailboxDisabled);
    }

    [Fact]
    public void The_audit_row_says_who_sent_from_which_mailbox_never_to_whom_or_what()
    {
        var payload = SanitizedJsonSerializer.Serialize(Command(Guid.NewGuid(), to: ["aya@example.ci"], subject: "Prêt 2 000 000",
            attachments: [new OutgoingAttachment("rib.pdf", "application/pdf", Encoding.UTF8.GetBytes("RIB-SECRET"))]));

        payload.Should().NotContain("aya@example.ci").And.NotContain("Prêt").And.NotContain("rib.pdf")
            .And.NotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("RIB-SECRET")));
    }

    // ── MIME ───────────────────────────────────────────────────────────────────

    [Fact]
    public void A_mime_reply_carries_In_Reply_To_and_References_a_text_part_and_its_attachments()
    {
        var message = OutgoingMime.Build(new OutgoingMail(
            new MailParty("awa@cabinet.ci", "Awa Koné"),
            [new MailParty("aya@example.ci", null)], [], [new MailParty("chef@cabinet.ci", null)],
            "RE: Votre dossier", "<p>Bonjour Aya,<br>voici le document.</p>",
            [new OutgoingAttachment("offre.pdf", "application/pdf", [1, 2])],
            new OutgoingReplyTo("m2", "<m2@mail>", "<m1@mail>")));

        message.InReplyTo.Should().Be("m2@mail");
        message.References.Should().Equal("m1@mail", "m2@mail");
        message.Bcc.Mailboxes.Single().Address.Should().Be("chef@cabinet.ci");
        message.TextBody.Should().Be("Bonjour Aya,\nvoici le document.");
        message.HtmlBody.Should().Contain("<p>Bonjour Aya");
        message.Attachments.OfType<MimePart>().Single().FileName.Should().Be("offre.pdf");
    }

    // ── Read flags, attachments ────────────────────────────────────────────────

    [Fact]
    public async Task Marking_a_conversation_read_goes_to_the_provider_first_and_a_refused_message_stays_unread()
    {
        var account = await AccountAsync();
        var first = await MessageAsync(account, "m1");
        await MessageAsync(account, "m2");
        await MessageAsync(account, "m3", isRead: true);
        _mailbox.RefuseFlag.Add("m2");

        await using (var db = _factory.CreateContext())
        {
            (await new SetMailThreadReadHandler(db, _currentUser, [_mailbox], new FixedClock(_now), NullLogger<SetMailThreadReadHandler>.Instance)
                .Handle(new SetMailThreadReadCommand(first.Id, true), CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        _mailbox.Flags.Should().Equal(("m1", true));

        await using var read = _factory.CreateContext();
        (await read.ConnectedMailMessages.OrderBy(m => m.ExternalId).Select(m => m.IsRead).ToListAsync()).Should().Equal(true, false, true);
    }

    [Fact]
    public async Task An_attachment_is_fetched_from_the_provider_under_its_stored_name_for_its_owner_only()
    {
        var account = await AccountAsync();
        var message = await MessageAsync(account, "m1", [new MailAttachmentInfo("att-1", "rib.pdf", "application/pdf", 3, false)]);
        _mailbox.Attachments["m1/att-1"] = [7, 8, 9];

        await using var db = _factory.CreateContext();
        var file = await new GetMailAttachmentHandler(db, _currentUser, [_mailbox], Sync(db))
            .Handle(new GetMailAttachmentQuery(message.Id, "att-1"), CancellationToken.None);

        file.Value.Should().BeEquivalentTo(new MailAttachmentFile("rib.pdf", "application/pdf", [7, 8, 9]));

        var colleague = Substitute.For<ICurrentUser>();
        colleague.Id.Returns(Guid.NewGuid());
        (await new GetMailAttachmentHandler(db, colleague, [_mailbox], Sync(db)).Handle(new GetMailAttachmentQuery(message.Id, "att-1"), CancellationToken.None))
            .Error.Should().Be(MailSendErrors.MessageNotFound);
    }

    // ── Templates ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_template_is_filled_for_a_client_html_encoded_and_names_what_it_could_not_fill()
    {
        var clientId = Guid.NewGuid();
        var customers = Substitute.For<ICustomersModule>();
        customers.GetClientSummaryAsync(_tenant, clientId, Arg.Any<CancellationToken>())
            .Returns(new ClientSummary(clientId, "CLI-0042", "Individual", "Koné & Fils", "Active", Guid.NewGuid(), null, "Approved", "Low", null));
        customers.CanActorAccessClientAsync(_tenant, clientId, _me, Arg.Any<CancellationToken>()).Returns(true);
        var tenants = Substitute.For<ITenantStore>();
        tenants.GetAsync(_tenant, Arg.Any<CancellationToken>()).Returns((TenantInfo?)null);

        var template = await TemplateAsync("Relance", "Votre dossier {{client.numero}}",
            "<p>Bonjour {{ destinataire.nom }},</p><p>{{institution}} — {{agent.nom}}, {{agent.email}}</p>");

        await using var db = _factory.CreateContext();
        var rendered = await new MailTemplateRenderer(db, _currentUser, tenants, new FixedClock(_now), customers: customers)
            .Handle(new RenderMailTemplateQuery(template.Id, new RenderMailTemplateRequest(null, clientId, null)), CancellationToken.None);

        rendered.Value!.Subject.Should().Be("Votre dossier CLI-0042");
        rendered.Value.Body.Should().Be("<p>Bonjour Koné &amp; Fils,</p><p> — Awa Koné, </p>");
        rendered.Value.Missing.Should().Equal("agent.email", "institution");

        // Outside the caller's perimeter the record is not read: its name and number stay empty.
        var outsider = Substitute.For<ICurrentUser>();
        outsider.Id.Returns(Guid.NewGuid());
        outsider.TenantId.Returns(_tenant);
        customers.ClearReceivedCalls();
        var blind = await new MailTemplateRenderer(db, outsider, tenants, new FixedClock(_now), customers: customers)
            .Handle(new RenderMailTemplateQuery(template.Id, new RenderMailTemplateRequest(null, clientId, null)), CancellationToken.None);
        blind.Value!.Subject.Should().Be("Votre dossier ");
        blind.Value.Missing.Should().Contain(["client.numero", "destinataire.nom"]);
        await customers.DidNotReceive().GetClientSummaryAsync(_tenant, clientId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Templates_are_validated_and_an_archived_one_is_neither_listed_nor_usable_by_an_agent()
    {
        await using (var db = _factory.CreateContext())
        {
            (await new SaveMailTemplateHandler(db, _currentUser, new FixedClock(_now))
                .Handle(new SaveMailTemplateCommand(null, " ", "Sujet", "<p>x</p>"), CancellationToken.None)).Error.Should().Be("MAIL_TEMPLATE_NAME_INVALID");
        }

        var template = await TemplateAsync("Ancien", "Sujet", "<p>x</p>");
        await using (var db = _factory.CreateContext())
        {
            (await new ArchiveMailTemplateHandler(db, _currentUser, new FixedClock(_now))
                .Handle(new ArchiveMailTemplateCommand(template.Id, true), CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        await using var read = _factory.CreateContext();
        _currentUser.Permissions.Returns([]);
        (await new ListMailTemplatesHandler(read, _currentUser).Handle(new ListMailTemplatesQuery(true), CancellationToken.None)).Should().BeEmpty();

        _currentUser.Permissions.Returns([Permissions.CanManageMailTemplates.Code]);
        (await new ListMailTemplatesHandler(read, _currentUser).Handle(new ListMailTemplatesQuery(true), CancellationToken.None)).Should().ContainSingle();

        (await new MailTemplateRenderer(read, _currentUser, Substitute.For<ITenantStore>(), new FixedClock(_now))
                .Handle(new RenderMailTemplateQuery(template.Id, new RenderMailTemplateRequest(null, null, null)), CancellationToken.None))
            .Error.Should().Be(MailTemplateRenderer.NotFound);
    }

    // ── Providers ──────────────────────────────────────────────────────────────

    private sealed class Routes : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Json)> _answers = new();
        public List<(HttpMethod Method, string Url, bool Authorised, string? ContentType, string? Range, byte[] Body)> Asked { get; } = [];

        public Routes Then(string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _answers.Enqueue((status, json));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add((request.Method, Uri.UnescapeDataString(request.RequestUri!.ToString()), request.Headers.Authorization is not null,
                request.Content?.Headers.ContentType?.MediaType, request.Content?.Headers.ContentRange?.ToString(),
                request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct)));

            var (status, json) = _answers.Count > 0 ? _answers.Dequeue() : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static OutgoingMail Mail(OutgoingReplyTo? replyTo = null, params OutgoingAttachment[] attachments) => new(
        new MailParty("awa@cabinet.ci", null), [new MailParty("aya@example.ci", "Aya")], [], [], "Votre dossier", "<p>Bonjour</p>", attachments, replyTo);

    [Fact]
    public async Task Graph_replies_through_a_draft_uploads_a_large_attachment_in_unauthorised_chunks_then_sends()
    {
        var routes = new Routes()
            .Then("""{ "id": "draft-1" }""")                                   // createReply
            .Then("{}")                                                         // PATCH
            .Then("{}")                                                         // small attachment
            .Then("""{ "uploadUrl": "https://outlook.office.com/upload/abc" }""") // createUploadSession
            .Then("{}").Then("{}")                                              // two chunks
            .Then("", HttpStatusCode.Accepted);                                // send

        var big = new byte[GraphMailboxReader.UploadChunk + 10];
        await new GraphMailboxReader(new HttpClient(routes), null!).SendAsync(new TokenMailboxSession("t"), null!,
            Mail(new OutgoingReplyTo("AAMk1", "<a@b>", "conv"), new OutgoingAttachment("petit.pdf", "application/pdf", [1]),
                new OutgoingAttachment("gros.pdf", "application/pdf", big)), CancellationToken.None);

        routes.Asked.Select(a => (a.Method.Method, Path: a.Url.Split('?')[0])).Should().Equal(
            ("POST", "https://graph.microsoft.com/v1.0/me/messages/AAMk1/createReply"),
            ("PATCH", "https://graph.microsoft.com/v1.0/me/messages/draft-1"),
            ("POST", "https://graph.microsoft.com/v1.0/me/messages/draft-1/attachments"),
            ("POST", "https://graph.microsoft.com/v1.0/me/messages/draft-1/attachments/createUploadSession"),
            ("PUT", "https://outlook.office.com/upload/abc"),
            ("PUT", "https://outlook.office.com/upload/abc"),
            ("POST", "https://graph.microsoft.com/v1.0/me/messages/draft-1/send"));

        var chunks = routes.Asked.Where(a => a.Method == HttpMethod.Put).ToList();
        chunks.Should().OnlyContain(c => !c.Authorised, "the upload URL is pre-authorised and refuses a bearer token");
        chunks[0].Range.Should().Be($"bytes 0-{GraphMailboxReader.UploadChunk - 1}/{big.Length}");
        chunks[1].Range.Should().Be($"bytes {GraphMailboxReader.UploadChunk}-{big.Length - 1}/{big.Length}");
    }

    [Fact]
    public async Task A_Graph_send_refused_on_the_way_deletes_its_draft()
    {
        var routes = new Routes()
            .Then("""{ "id": "draft-1" }""")
            .Then("""{ "error": { "code": "ErrorMessageSizeExceeded" } }""", HttpStatusCode.BadRequest)
            .Then("", HttpStatusCode.NoContent);

        await new GraphMailboxReader(new HttpClient(routes), null!)
            .Invoking(r => r.SendAsync(new TokenMailboxSession("t"), null!, Mail(null, new OutgoingAttachment("a.pdf", "application/pdf", [1])), CancellationToken.None))
            .Should().ThrowAsync<MailSendException>().Where(e => e.Code == MailSendException.Refused);

        routes.Asked[^1].Method.Should().Be(HttpMethod.Delete);
        routes.Asked[^1].Url.Should().EndWith("/messages/draft-1");
    }

    [Fact]
    public async Task Gmail_sends_a_new_message_as_media_and_a_reply_as_related_parts_naming_its_thread()
    {
        var routes = new Routes().Then("""{ "id": "g9" }""").Then("""{ "id": "g10" }""");
        var reader = new GmailMailboxReader(new HttpClient(routes), null!);

        await reader.SendAsync(new TokenMailboxSession("t"), null!, Mail(), CancellationToken.None);
        await reader.SendAsync(new TokenMailboxSession("t"), null!, Mail(new OutgoingReplyTo("g1", "<g1@mail>", "th-1")), CancellationToken.None);

        routes.Asked[0].Url.Should().EndWith("uploadType=media");
        routes.Asked[0].ContentType.Should().Be("message/rfc822");
        Encoding.UTF8.GetString(routes.Asked[0].Body).Should().Contain("Subject: Votre dossier");

        routes.Asked[1].Url.Should().EndWith("uploadType=multipart");
        routes.Asked[1].ContentType.Should().Be("multipart/related");
        var related = Encoding.UTF8.GetString(routes.Asked[1].Body);
        related.Should().Contain("{\"threadId\":\"th-1\"}").And.Contain("In-Reply-To: <g1@mail>");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static SendMailCommand Command(
        Guid accountId, IReadOnlyList<string>? to = null, IReadOnlyList<string>? cc = null, string subject = "Votre dossier",
        Guid? replyTo = null, Guid? forward = null, IReadOnlyList<string>? forwardAttachments = null, IReadOnlyList<OutgoingAttachment>? attachments = null) =>
        new(accountId, to ?? ["aya@example.ci"], cc ?? [], [], subject, "<p>Bonjour</p>", replyTo, forward, forwardAttachments ?? [], attachments ?? []);

    private ConnectedMailboxSync Sync(IntegrationDbContext db) => new(
        db, [_mailbox], new CalendarEventProtector(new PrefixEncryptor()), new FixedClock(_now), NullLogger<ConnectedMailboxSync>.Instance);

    private async Task<Result<SendMailOutcome>> SendAsync(SendMailCommand command, ICurrentUser? user = null, ICustomersModule? customers = null)
    {
        await using var db = _factory.CreateContext();
        return await new SendMailHandler(db, user ?? _currentUser, [_mailbox], Sync(db), NullLogger<SendMailHandler>.Instance, _jobs, customers,
                new CalendarEventProtector(new PrefixEncryptor()), new FixedClock(_now))
            .Handle(command, CancellationToken.None);
    }

    private async Task StatusShouldBe(Guid accountId, ConnectedAccountStatus status)
    {
        await using var db = _factory.CreateContext();
        (await db.ConnectedAccounts.SingleAsync(a => a.Id == accountId)).Status.Should().Be(status);
    }

    private async Task<ConnectedAccount> AccountAsync(string email = "awa@cabinet.ci", bool mail = true)
    {
        var account = ConnectedAccount.Connect(_tenant, _me, ConnectedAccountProvider.Google, $"ext-{Guid.NewGuid():N}", email, null, null, "openid", _now);
        if (mail) account.ApplyMailGrant(true, _now);

        await using var db = _factory.CreateContext();
        db.ConnectedAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private async Task<ConnectedMailMessage> MessageAsync(ConnectedAccount account, string externalId, List<MailAttachmentInfo>? attachments = null, bool isRead = false)
    {
        var message = ConnectedMailMessage.Create(_tenant, account.Id, _me, externalId, $"<{externalId}@mail>", "thread-1",
            MailFolderKind.Inbox, MailDirection.Received, _now, isRead, attachments is { Count: > 0 }, _now);

        await using var db = _factory.CreateContext();
        var protector = new CalendarEventProtector(new PrefixEncryptor());
        message.SetContent(null, null, null, protector.Protect("Votre dossier"), null, protector.Protect("<p>x</p>"), true, false,
            attachments is null ? null : protector.Protect(System.Text.Json.JsonSerializer.Serialize(attachments, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))));
        db.ConnectedMailMessages.Add(message);
        await db.SaveChangesAsync();
        return message;
    }

    private async Task<MailTemplate> TemplateAsync(string name, string subject, string body)
    {
        var template = MailTemplate.Create(_tenant, name, subject, body, _me, _now).Value!;
        await using var db = _factory.CreateContext();
        db.MailTemplates.Add(template);
        await db.SaveChangesAsync();
        return template;
    }
}

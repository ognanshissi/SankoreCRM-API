namespace Sankore.Modules.Integration.Tests.Features.Messaging;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Adapters.Dialog360;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Messaging;
using Sankore.Modules.Integration.Features.Messaging.Webhook;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.Features.Telephony;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class MessagingTests : IDisposable
{
    private const string Token = "a-dev-only-webhook-token-at-least-32-bytes";
    private const string NumberId = "109876543210";

    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;

    public MessagingTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        db.Connections.Add(ActiveNumber(_tenant, _connection, NumberId));
        db.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    // ── The Cloud API body ─────────────────────────────────────────────────────

    [Fact]
    public void A_text_message_is_read_with_its_sender_profile_and_number()
    {
        var body = CloudApiWebhook.BuildInboundText(NumberId, "+2250708091801", "Awa", "Bonjour", "wamid.A", Now);

        var batch = CloudApiWebhook.Parse(body);

        batch.PhoneNumberIds.Should().Equal(NumberId);
        var message = batch.Messages.Should().ContainSingle().Subject;
        message.From.Should().Be("+2250708091801");
        message.ProfileName.Should().Be("Awa");
        message.Text.Should().Be("Bonjour");
        message.Type.Should().Be("text");
        message.Timestamp.Should().Be(Now);
    }

    [Fact]
    public void A_failed_status_carries_Meta_s_code_and_its_translation()
    {
        const string body = """
            {"entry":[{"changes":[{"value":{"metadata":{"phone_number_id":"109876543210"},
             "statuses":[{"id":"wamid.B","status":"failed","timestamp":"1792573200",
             "errors":[{"code":131047,"title":"Re-engagement message"}]}]}}]}]}
            """;

        var status = CloudApiWebhook.Parse(body).Statuses.Should().ContainSingle().Subject;

        status.Status.Should().Be("failed");
        status.ErrorCode.Should().Be("131047");
        CloudApiErrors.TranslateStatusError(status.ErrorCode).Should().Be(IntegrationErrors.MessagingOutsideWindow);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("https://waba-v2.360dialog.io", true)]
    [InlineData("https://WABA-SANDBOX.360dialog.io/", true)]
    [InlineData("https://attacker.example", false)]
    [InlineData("https://waba-v2.360dialog.io.attacker.example", false)]
    [InlineData("https://user@waba-v2.360dialog.io", false)]
    [InlineData("https://waba-v2.360dialog.io:8443", false)]
    [InlineData("http://waba-v2.360dialog.io", false)]
    public void The_number_s_key_is_only_ever_sent_to_a_360dialog_host(string? url, bool allowed)
        => Dialog360Settings.IsAllowedApiBaseUrl(url).Should().Be(allowed);

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"entry\":42}")]
    [InlineData("")]
    public void A_body_of_another_shape_is_an_empty_batch_never_an_exception(string body)
        => CloudApiWebhook.Parse(body).Should().BeEquivalentTo(ConversationalWebhookBatch.Empty);

    [Fact]
    public void The_routing_read_finds_the_number_and_nothing_else()
    {
        var body = Encoding.UTF8.GetBytes(
            CloudApiWebhook.BuildInboundText(NumberId, "+2250708091801", null, "x", "wamid.C", Now));

        CloudApiWebhook.ReadPhoneNumberIds(body).Should().Equal(NumberId);
        CloudApiWebhook.ReadPhoneNumberIds("{not json"u8).Should().BeEmpty();
    }

    // ── Verification ───────────────────────────────────────────────────────────

    [Fact]
    public void The_token_or_Meta_s_signature_verifies_and_nothing_else_does()
    {
        var body = "{\"entry\":[]}"u8.ToArray();
        var hmac = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Token), body)).ToLowerInvariant();

        MessagingWebhookEndpoints.Verify(Token, Token, null, body).Should().BeTrue();
        MessagingWebhookEndpoints.Verify(Token, null, hmac, body).Should().BeTrue();
        MessagingWebhookEndpoints.Verify(Token, "wrong", null, body).Should().BeFalse();
        MessagingWebhookEndpoints.Verify(Token, null, "sha256=00ff", body).Should().BeFalse();
        MessagingWebhookEndpoints.Verify(Token, null, null, body).Should().BeFalse();
    }

    [Fact]
    public async Task A_verified_delivery_is_stored_encrypted_and_queued_with_identifiers_only()
    {
        var body = Encoding.UTF8.GetBytes(
            CloudApiWebhook.BuildInboundText(NumberId, "+2250708091801", "Awa", "Mon numéro est le 0708091801", "wamid.D", Now));
        var hangfire = Substitute.For<IBackgroundJobClient>();

        (await AcceptAsync(body, Token, hangfire)).Should().BeTrue();
        (await AcceptAsync(body, Token, hangfire)).Should().BeTrue("a redelivery is answered 200 and does no work");

        var job = Jobs(hangfire).Should().ContainSingle().Subject;
        job.Args.Should().HaveCount(3);
        job.Args[0].Should().Be(_tenant);
        job.Args[1].Should().Be(_connection);

        await using var after = _factory.CreateContext();
        var stored = after.MsgWebhookEvents.Should().ContainSingle().Subject;
        Encoding.UTF8.GetString(stored.PayloadEnc).Should().StartWith("v1:").And.NotContain("0708091801");
    }

    [Fact]
    public async Task An_unknown_number_or_a_wrong_token_is_refused_and_stores_nothing()
    {
        var unknown = Encoding.UTF8.GetBytes(CloudApiWebhook.BuildInboundText("555", "+2250708091801", null, "x", "wamid.E", Now));
        var known = Encoding.UTF8.GetBytes(CloudApiWebhook.BuildInboundText(NumberId, "+2250708091801", null, "x", "wamid.F", Now));

        (await AcceptAsync(unknown, Token)).Should().BeFalse();
        (await AcceptAsync(known, "not-the-token")).Should().BeFalse();

        await using var after = _factory.CreateContext();
        after.MsgWebhookEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task The_job_hands_the_messages_to_M13_and_finishes_the_event()
    {
        var body = Encoding.UTF8.GetBytes(
            CloudApiWebhook.BuildInboundText(NumberId, "+2250708091801", "Awa", "Bonjour", "wamid.G", Now));

        (await AcceptAsync(body, Token)).Should().BeTrue();

        var leads = Substitute.For<ILeadsModule>();
        leads.RecordMessagingEventsAsync(default, default, default, default!, default)
            .ReturnsForAnyArgs(new MessagingEventsOutcome(1, 0, 0, 0));

        Guid eventId;
        await using (var read = _factory.CreateContext()) eventId = read.MsgWebhookEvents.Single().Id;

        await using (var db = _factory.CreateContext())
        {
            await ProcessMessagingWebhookJob.RunAsync(
                db, Channels(db), TelephonyTestHarness.Payloads(), leads, new FixedClock(Now),
                NullLogger.Instance, _tenant, _connection, eventId, CancellationToken.None);
        }

        await leads.Received(1).RecordMessagingEventsAsync(
            _tenant, _connection, null,
            Arg.Is<MessagingEventBatch>(b => b.Messages.Count == 1 && b.Messages[0].Text == "Bonjour"),
            Arg.Any<CancellationToken>());

        await using var after = _factory.CreateContext();
        after.MsgWebhookEvents.Single().ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_status_that_overtook_its_message_is_retried_while_recent()
    {
        var body = Encoding.UTF8.GetBytes(CloudApiWebhook.BuildStatus(NumberId, "wamid.H", "sent", Now));

        (await AcceptAsync(body, Token)).Should().BeTrue();

        var leads = Substitute.For<ILeadsModule>();
        leads.RecordMessagingEventsAsync(default, default, default, default!, default)
            .ReturnsForAnyArgs(new MessagingEventsOutcome(0, 0, 0, 1));

        await using var db = _factory.CreateContext();
        var eventId = db.MsgWebhookEvents.Single().Id;

        var run = () => ProcessMessagingWebhookJob.RunAsync(
            db, Channels(db), TelephonyTestHarness.Payloads(), leads, new FixedClock(Now.AddMinutes(1)),
            NullLogger.Instance, _tenant, _connection, eventId, CancellationToken.None);

        await run.Should().ThrowAsync<InvalidOperationException>("Hangfire comes back for it");
    }

    // ── Number uniqueness ──────────────────────────────────────────────────────

    [Fact]
    public void The_channel_address_is_copied_from_the_settings_and_never_bound()
    {
        var connection = ActiveNumber(_tenant, Guid.NewGuid(), " 42 ");
        connection.ChannelAddress.Should().Be("42");

        new RingoverSettings().ChannelAddress.Should().BeNull();
    }

    // ── 360dialog ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(131047, IntegrationErrors.MessagingOutsideWindow, ErrorFamily.Functional)]
    [InlineData(132001, IntegrationErrors.MessagingTemplateRejected, ErrorFamily.Functional)]
    [InlineData(131026, IntegrationErrors.MessagingRecipientInvalid, ErrorFamily.Functional)]
    [InlineData(130429, IntegrationErrors.MessagingQuotaExceeded, ErrorFamily.Transient)]
    [InlineData(190, IntegrationErrors.AuthenticationRefused, ErrorFamily.Technical)]
    public void Meta_s_codes_are_translated(int code, string expected, ErrorFamily family)
    {
        var result = Dialog360ErrorClassification.Classify(
            HttpStatusCode.BadRequest, $"{{\"error\":{{\"code\":{code},\"title\":\"x\"}}}}", "send");

        result.Code.Should().Be(expected);
        result.Family.Should().Be(family);
    }

    [Fact]
    public void An_unknown_refusal_falls_back_on_the_status_and_never_retries_a_4xx()
    {
        Dialog360ErrorClassification.Classify(HttpStatusCode.BadRequest, "<html>", "send")
            .Family.Should().Be(ErrorFamily.Functional);

        Dialog360ErrorClassification.Classify(HttpStatusCode.TooManyRequests, null, "send")
            .Family.Should().Be(ErrorFamily.Transient);
    }

    [Theory]
    [InlineData(429, false, true)]
    [InlineData(503, false, true)]
    [InlineData(502, false, false)]   // a proxy gave up: Meta may have accepted — never resent
    [InlineData(504, true, true)]     // a read may be asked again
    [InlineData(400, true, false)]
    public void Only_what_cannot_double_send_is_retried(int status, bool isRead, bool retried)
        => Dialog360Transport.IsRetryable((HttpStatusCode)status, isRead).Should().Be(retried);

    [Fact]
    public void The_template_catalogue_is_read_with_its_components()
    {
        const string json = """
            {"total":1,"waba_templates":[{"name":"premier_contact","language":"fr","category":"UTILITY",
             "status":"approved","id":"t1","components":[
               {"type":"HEADER","format":"TEXT","text":"Bienvenue"},
               {"type":"BODY","text":"Bonjour {{1}}"},
               {"type":"FOOTER","text":"STOP pour arrêter"},
               {"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Oui"}]}]}]}
            """;

        var (templates, total) = Dialog360Templates.Read(json);

        total.Should().Be(1);
        var template = templates.Should().ContainSingle().Subject;
        template.Status.Should().Be("APPROVED");
        template.HeaderText.Should().Be("Bienvenue");
        template.BodyText.Should().Be("Bonjour {{1}}");
        template.Buttons.Should().Equal("Oui");
    }

    [Fact]
    public void The_sent_message_id_is_read_from_the_answer()
        => Dialog360Messages.ReadMessageId("""{"messages":[{"id":"wamid.Z"}]}""").Should().Be("wamid.Z");

    // ── Helpers ────────────────────────────────────────────────────────────────

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    internal static IntegrationConnection ActiveNumber(Guid tenantId, Guid id, string phoneNumberId)
    {
        var clock = new FixedClock(Now);

        var connection = IntegrationConnection.Create(
            tenantId: tenantId,
            family: IntegrationFamily.Messaging,
            kind: IntegrationKind.Fake,
            mode: IntegrationMode.Api,
            name: "WhatsApp test number",
            settings: new FakeSettings { Family = IntegrationFamily.Messaging, PhoneNumberId = phoneNumberId },
            createdBy: Guid.NewGuid(),
            clock: clock,
            id: id);

        connection.RecordHealth(IntegrationHealth.Healthy(TimeSpan.FromMilliseconds(10), Now), clock);
        connection.Activate(Guid.NewGuid(), clock).IsSuccess.Should().BeTrue();

        return connection;
    }

    private static MessagingChannels Channels(IntegrationDbContext db)
        => new(db, TelephonyTestHarness.Resolver(db, new FakeAdapter()), cache: null, NullLogger<MessagingChannels>.Instance);

    private ISecretsModule Secrets()
    {
        var secrets = Substitute.For<ISecretsModule>();
        secrets.GetValueAsync(IntegrationSecrets.WebhookSecretKey(_tenant, _connection), Arg.Any<CancellationToken>())
            .Returns(Token);
        return secrets;
    }

    private async Task<bool> AcceptAsync(byte[] body, string? token, IBackgroundJobClient? hangfire = null)
    {
        await using var db = _factory.CreateContext();

        return await MessagingWebhookEndpoints.AcceptAsync(
            "fake", body, token, signature: null, db, Channels(db), TelephonyTestHarness.Payloads(), Secrets(),
            hangfire ?? Substitute.For<IBackgroundJobClient>(), new FixedClock(Now), NullLogger.Instance,
            CancellationToken.None);
    }

    private static IReadOnlyList<Job> Jobs(IBackgroundJobClient hangfire)
        => hangfire.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<Job>()
            .Where(j => j.Type == typeof(ProcessMessagingWebhookJob))
            .ToList();
}

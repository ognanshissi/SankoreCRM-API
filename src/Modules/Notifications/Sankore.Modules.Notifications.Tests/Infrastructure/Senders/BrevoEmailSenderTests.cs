namespace Sankore.Modules.Notifications.Tests.Infrastructure.Senders;

using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Notifications.Infrastructure.Providers;
using Sankore.Modules.Notifications.Infrastructure.Senders;
using Sankore.Modules.Notifications.Infrastructure.Senders.Brevo;
using Xunit;

public sealed class BrevoEmailSenderTests
{
    /// <summary>Captures the outgoing request and answers with whatever the test wants.</summary>
    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Payload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Payload = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FixedCredentials(string? credential) : INotificationCredentials
    {
        public Task<string?> GetAsync(Guid tenantId, ResolvedEmailProvider provider, CancellationToken ct)
            => Task.FromResult(credential);
    }

    private static ResolvedEmailProvider BrevoProvider(bool hasCredential = true) => new(
        "Brevo", false, "noreply@mfi.ci", "MFI", null, null,
        hasCredential, null, null, null, false, false);

    private static SendEmailRequest Message() => new(
        MessageId: Guid.NewGuid(),
        TenantId: Guid.NewGuid(),
        FromEmail: "noreply@mfi.ci",
        FromName: "MFI",
        ReplyToEmail: "support@mfi.ci",
        ToEmail: "awa@example.ci",
        ToName: "Awa Ouattara",
        Subject: "Bienvenue",
        HtmlBody: "<p>Bonjour</p>",
        TextBody: "Bonjour");

    private static (BrevoEmailSender Sender, CapturingHandler Handler) Build(
        string? credential = "xkeysib-abc",
        HttpStatusCode status = HttpStatusCode.Created,
        string body = """{"messageId":"<202609@brevo>"}""")
    {
        var handler = new CapturingHandler(status, body);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(BrevoEmailSender.HttpClientName).Returns(_ => new HttpClient(handler));

        return (new BrevoEmailSender(
            factory, new FixedCredentials(credential), NullLogger<BrevoEmailSender>.Instance), handler);
    }

    [Fact]
    public async Task Posts_to_the_transactional_endpoint_with_the_api_key_header()
    {
        var (sender, handler) = Build();

        await sender.SendAsync(Message(), BrevoProvider(), CancellationToken.None);

        handler.Request!.Method.Should().Be(HttpMethod.Post);
        handler.Request.RequestUri!.ToString().Should().Be("https://api.brevo.com/v3/smtp/email");

        // Brevo authenticates on "api-key", not on Authorization — getting this wrong yields a
        // 401 that looks like a bad key.
        handler.Request.Headers.GetValues("api-key").Should().ContainSingle().Which.Should().Be("xkeysib-abc");
    }

    [Fact]
    public async Task Sends_the_payload_brevo_expects()
    {
        var (sender, handler) = Build();

        await sender.SendAsync(Message(), BrevoProvider(), CancellationToken.None);

        handler.Payload.Should().Contain("\"sender\"").And.Contain("noreply@mfi.ci");
        handler.Payload.Should().Contain("\"to\"").And.Contain("awa@example.ci");
        handler.Payload.Should().Contain("\"replyTo\"").And.Contain("support@mfi.ci");
        handler.Payload.Should().Contain("\"htmlContent\"");
        handler.Payload.Should().Contain("\"subject\":\"Bienvenue\"");
    }

    [Fact]
    public async Task Omits_replyTo_entirely_when_there_is_none()
    {
        var (sender, handler) = Build();
        var message = Message() with { ReplyToEmail = null };

        await sender.SendAsync(message, BrevoProvider(), CancellationToken.None);

        handler.Payload.Should().NotContain("replyTo", "a null replyTo makes Brevo reject the call");
    }

    [Fact]
    public async Task Throws_when_brevo_refuses_and_carries_its_reason()
    {
        // The outbox needs the failure to retry and eventually dead-letter; swallowing it would
        // report the message as delivered.
        var (sender, _) = Build(
            status: HttpStatusCode.BadRequest,
            body: """{"code":"invalid_parameter","message":"sender is not valid"}""");

        var act = async () => await sender.SendAsync(Message(), BrevoProvider(), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("sender is not valid").And.Contain("400");
    }

    [Fact]
    public async Task Refuses_to_send_when_no_api_key_is_stored()
    {
        var (sender, handler) = Build(credential: null);

        var act = async () => await sender.SendAsync(
            Message(), BrevoProvider(hasCredential: false), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("no API key");

        handler.Request.Should().BeNull("nothing should reach the network without a key");
    }
}

namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.Snippet;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class SnippetHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IOptions<SnippetOptions> _options;

    public SnippetHandlerTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _options = Options.Create(new SnippetOptions
        {
            SdkUrl = "https://cdn.sankore.io/sdk/v1/sankore-forms.min.js",
            SriHash = "sha384-testHash123"
        });
    }

    public void Dispose() => _factory.Dispose();

    private async Task<LeadSourceConfig> SeedEmbeddedSource()
    {
        await using var db = _factory.CreateContext();
        var source = LeadSourceConfig.Create(
            _tenantId, "WEB", "Web Form",
            LeadChannelType.WebForm, 0,
            IntegrationMode.EmbeddedScript,
            settings: new EmbeddedScriptSettings
            {
                AllowedOrigins = ["https://example.com"],
                FormContainerId = "my-form"
            });
        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync();
        return source;
    }

    private async Task<LeadSourceConfig> SeedWebhookSource()
    {
        await using var db = _factory.CreateContext();
        var source = LeadSourceConfig.Create(
            _tenantId, "HOOK", "Webhook",
            LeadChannelType.InboundWebhook, 0,
            IntegrationMode.ServerWebhook);
        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync();
        return source;
    }

    [Fact]
    public async Task GetSnippet_returns_html_with_sdk_and_publickey()
    {
        var source = await SeedEmbeddedSource();

        await using var db = _factory.CreateContext();
        var handler = new GetSnippetHandler(db, _options);
        var result = await handler.Handle(new GetSnippetQuery(source.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Html.Should().Contain(source.PublicKey!);
        result.Value.Html.Should().Contain("sankore-forms.min.js");
        result.Value.Html.Should().Contain("sha384-testHash123");
        result.Value.Html.Should().Contain("my-form");
        result.Value.SdkUrl.Should().Contain("sankore-forms.min.js");
        result.Value.SriHash.Should().Be("sha384-testHash123");
        result.Value.PublicKey.Should().Be(source.PublicKey);
        result.Value.FormContainerId.Should().Be("my-form");
    }

    [Fact]
    public async Task GetSnippet_fails_for_non_embedded_mode()
    {
        var source = await SeedWebhookSource();

        await using var db = _factory.CreateContext();
        var handler = new GetSnippetHandler(db, _options);
        var result = await handler.Handle(new GetSnippetQuery(source.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("NOT_EMBEDDED_SCRIPT_MODE");
    }

    [Fact]
    public async Task GetSnippet_fails_for_unknown_source()
    {
        await using var db = _factory.CreateContext();
        var handler = new GetSnippetHandler(db, _options);
        var result = await handler.Handle(new GetSnippetQuery(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("SOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task SendSnippet_queues_email_via_notifications_module()
    {
        var source = await SeedEmbeddedSource();
        var notif = Substitute.For<INotificationsModule>();
        notif.QueueEmailAsync(Arg.Any<QueueEmailRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<Guid>.Ok(Guid.NewGuid()));

        await using var db = _factory.CreateContext();
        var handler = new SendSnippetHandler(db, notif, _options);
        var result = await handler.Handle(
            new SendSnippetCommand(source.Id, "webmaster@example.com"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await notif.Received(1).QueueEmailAsync(
            Arg.Is<QueueEmailRequest>(r =>
                r.TemplateKey == "lead-source.snippet"
                && r.RecipientEmail == "webmaster@example.com"
                && r.TemplateData.ContainsKey("snippet")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendSnippet_fails_for_non_embedded_mode()
    {
        var source = await SeedWebhookSource();
        var notif = Substitute.For<INotificationsModule>();

        await using var db = _factory.CreateContext();
        var handler = new SendSnippetHandler(db, notif, _options);
        var result = await handler.Handle(
            new SendSnippetCommand(source.Id, "webmaster@example.com"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("NOT_EMBEDDED_SCRIPT_MODE");
    }
}

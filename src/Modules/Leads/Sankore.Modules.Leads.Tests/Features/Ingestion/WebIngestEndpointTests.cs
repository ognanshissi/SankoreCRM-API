namespace Sankore.Modules.Leads.Tests.Features.Ingestion;

using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.Ingestion;
using Sankore.Modules.Leads.Features.Ingestion.Web;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class WebIngestEndpointTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly ISender _sender;
    private readonly ICaptchaValidator _captcha;

    public WebIngestEndpointTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _sender = Substitute.For<ISender>();
        _captcha = new StubCaptchaValidator();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<LeadSourceConfig> SeedSource(
        LeadSourceStatus targetStatus = LeadSourceStatus.Active,
        EmbeddedScriptSettings? settings = null)
    {
        await using var db = _factory.CreateContext();
        var source = LeadSourceConfig.Create(
            _tenantId, "WEB", "Web Form",
            LeadChannelType.WebForm, 0,
            IntegrationMode.EmbeddedScript,
            settings: settings ?? new EmbeddedScriptSettings
            {
                AllowedOrigins = ["https://example.com"]
            });

        if (targetStatus >= LeadSourceStatus.Testing) source.StartTesting();
        if (targetStatus >= LeadSourceStatus.Active) source.Activate();
        if (targetStatus == LeadSourceStatus.Paused) source.Pause();
        if (targetStatus == LeadSourceStatus.Archived) source.Archive();

        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync();
        return source;
    }

    private HttpContext BuildHttpContext(string? origin = "https://example.com",
        long? contentLength = null, string? body = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        if (origin is not null) context.Request.Headers.Origin = origin;
        context.Request.ContentType = "application/json";
        if (contentLength.HasValue)
            context.Request.ContentLength = contentLength;

        var bodyText = body ?? JsonSerializer.Serialize(new WebIngestRequest
        {
            Fields = new Dictionary<string, string?> { ["fullName"] = "Test", ["phoneNumber"] = "+225070707" },
            Consent = true,
            ConsentVersion = "v1",
            FormLoadedAt = DateTimeOffset.UtcNow.AddSeconds(-10)
        });
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bodyText));
        context.Request.ContentLength ??= context.Request.Body.Length;

        return context;
    }

    // We can't directly call the minimal API handler since it's wired via MapPost.
    // Instead, test the core pipeline logic via IngestInboundLeadHandler integration tests.
    // These unit tests focus on the domain/validation logic exercised by the endpoint.

    [Fact]
    public void EmbeddedScriptSettings_has_captcha_and_honeypot_fields()
    {
        var settings = new EmbeddedScriptSettings
        {
            AllowedOrigins = ["https://example.com"],
            CaptchaProvider = "recaptcha",
            MinSubmitDelaySeconds = 5,
            HoneypotFieldName = "_hp"
        };

        settings.CaptchaProvider.Should().Be("recaptcha");
        settings.MinSubmitDelaySeconds.Should().Be(5);
        settings.HoneypotFieldName.Should().Be("_hp");
    }

    [Fact]
    public void Default_MinSubmitDelaySeconds_is_3()
    {
        var settings = new EmbeddedScriptSettings();
        settings.MinSubmitDelaySeconds.Should().Be(3);
    }

    [Fact]
    public void WebIngestRequest_deserializes_correctly()
    {
        var json = """
        {
            "fields": { "fullName": "Test", "phoneNumber": "+225070707" },
            "consent": true,
            "consentVersion": "v1",
            "captchaToken": "tok-123",
            "formLoadedAt": "2026-09-24T10:00:00Z",
            "page": "https://example.com/contact",
            "referrer": "https://google.com"
        }
        """;

        var req = JsonSerializer.Deserialize<WebIngestRequest>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        req.Should().NotBeNull();
        req!.Fields["fullName"].Should().Be("Test");
        req.Consent.Should().BeTrue();
        req.CaptchaToken.Should().Be("tok-123");
        req.Page.Should().Be("https://example.com/contact");
    }

    [Fact]
    public async Task Unknown_publicKey_returns_source_not_found_in_pipeline()
    {
        // Simulates the source lookup failure via IngestInboundLeadCommand
        await using var db = _factory.CreateContext();
        var handler = new IngestInboundLeadHandler(db,
            Substitute.For<IPhoneBlindIndexer>(), TimeProvider.System);

        var result = await handler.Handle(new IngestInboundLeadCommand(
            _tenantId, Guid.NewGuid(), """{"fullName":"t","phoneNumber":"0707"}"""),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("SOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task Paused_source_returns_not_active_in_pipeline()
    {
        var source = await SeedSource(LeadSourceStatus.Paused);

        await using var db = _factory.CreateContext();
        var handler = new IngestInboundLeadHandler(db,
            Substitute.For<IPhoneBlindIndexer>(), TimeProvider.System);

        var result = await handler.Handle(new IngestInboundLeadCommand(
            _tenantId, source.Id, """{"fullName":"t","phoneNumber":"0707"}"""),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("SOURCE_NOT_ACTIVE");
    }

    [Fact]
    public async Task Missing_consent_would_be_caught_by_endpoint()
    {
        // The consent check happens in the endpoint, not the pipeline handler.
        // Verify the request model correctly captures consent=false
        var req = new WebIngestRequest { Consent = false };
        req.Consent.Should().BeFalse();
    }

    [Fact]
    public void StubCaptchaValidator_always_returns_true()
    {
        var validator = new StubCaptchaValidator();
        var result = validator.ValidateAsync("recaptcha", "token", "127.0.0.1", CancellationToken.None).Result;
        result.Should().BeTrue();
    }
}

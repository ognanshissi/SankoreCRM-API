namespace Sankore.Modules.Notifications.Tests.Infrastructure.Rendering;

using FluentAssertions;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Infrastructure;
using Sankore.Modules.Notifications.Infrastructure.Rendering;
using Sankore.Modules.Notifications.Tests.TestSupport;
using Xunit;

public sealed class ScribanTemplateRendererTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private ScribanTemplateRenderer BuildRendererWithDb(
        Action<NotificationsDbContext> seed, Guid? tenantId = null)
    {
        var db = TestNotificationsDbContextFactory.Create(tenantId ?? _tenantId);
        seed(db);
        db.SaveChanges();
        return new ScribanTemplateRenderer(db, NullTestLogger<ScribanTemplateRenderer>.Instance);
    }

    [Fact]
    public async void Returns_stub_fallback_when_no_template_exists()
    {
        var renderer = new ScribanTemplateRenderer(
            TestNotificationsDbContextFactory.Create(_tenantId),
            NullTestLogger<ScribanTemplateRenderer>.Instance);

        var result = await renderer.RenderAsync(_tenantId, "no-such-template", "fr", "{}");

        result.Subject.Should().Contain("no-such-template");
        result.HtmlBody.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async void Uses_tenant_specific_template_over_platform()
    {
        var renderer = BuildRendererWithDb(db =>
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                null, "welcome", "fr", 1, "Platform Subject", "<p>platform</p>"));
            db.EmailTemplates.Add(EmailTemplate.Create(
                _tenantId, "welcome", "fr", 1, "Tenant Subject", "<p>tenant</p>"));
        });

        var result = await renderer.RenderAsync(_tenantId, "welcome", "fr", "{}");

        result.Subject.Should().Be("Tenant Subject");
        result.HtmlBody.Should().Contain("tenant");
    }

    [Fact]
    public async void Falls_back_to_platform_template_when_no_tenant_specific()
    {
        var renderer = BuildRendererWithDb(db =>
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                null, "welcome", "fr", 1, "Platform Subject", "<p>platform body</p>"));
        });

        var result = await renderer.RenderAsync(_tenantId, "welcome", "fr", "{}");

        result.Subject.Should().Be("Platform Subject");
        result.HtmlBody.Should().Contain("platform body");
    }

    [Fact]
    public async void Falls_back_to_en_locale_when_requested_locale_not_found()
    {
        var renderer = BuildRendererWithDb(db =>
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                null, "welcome", "en", 1, "English Subject", "<p>english</p>"));
        });

        var result = await renderer.RenderAsync(_tenantId, "welcome", "wolof", "{}");

        result.Subject.Should().Be("English Subject");
    }

    [Fact]
    public async void Does_not_fall_back_to_en_when_en_is_requested_but_only_fr_exists()
    {
        var renderer = BuildRendererWithDb(db =>
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                null, "welcome", "fr", 1, "French Only", "<p>fr</p>"));
        });

        var result = await renderer.RenderAsync(_tenantId, "welcome", "en", "{}");

        // No en template and we requested en (no double-fallback) — stub output
        result.Subject.Should().Contain("welcome");
        result.Subject.Should().NotBe("French Only");
    }

    [Fact]
    public async void Renders_Scriban_variables_from_json_payload()
    {
        var renderer = BuildRendererWithDb(db =>
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                _tenantId, "welcome", "fr", 1,
                "Bonjour {{ first_name }}",
                "<p>Bienvenue {{ first_name }} {{ last_name }}</p>"));
        });

        var data = """{"first_name":"Aminata","last_name":"Diallo"}""";
        var result = await renderer.RenderAsync(_tenantId, "welcome", "fr", data);

        result.Subject.Should().Be("Bonjour Aminata");
        result.HtmlBody.Should().Contain("Bienvenue Aminata Diallo");
    }

    [Fact]
    public async void Skips_inactive_templates_and_returns_stub_fallback()
    {
        var renderer = BuildRendererWithDb(db =>
        {
            var t = EmailTemplate.Create(null, "welcome", "fr", 1, "Active Subject", "<p></p>");
            t.Deactivate();
            db.EmailTemplates.Add(t);
        });

        var result = await renderer.RenderAsync(_tenantId, "welcome", "fr", "{}");

        result.Subject.Should().Contain("welcome");
        result.Subject.Should().NotBe("Active Subject");
    }

    [Fact]
    public async void Does_not_throw_on_invalid_scriban_syntax()
    {
        var renderer = BuildRendererWithDb(db =>
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                _tenantId, "broken", "fr", 1,
                "{{ unclosed",
                "<p>body</p>"));
        });

        var act = async () => await renderer.RenderAsync(_tenantId, "broken", "fr", "{}");
        await act.Should().NotThrowAsync();
    }
}

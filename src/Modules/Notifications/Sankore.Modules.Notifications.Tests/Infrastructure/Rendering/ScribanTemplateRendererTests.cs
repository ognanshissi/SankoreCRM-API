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
    public async void Falls_back_to_fr_locale_when_requested_locale_not_found()
    {
        var renderer = BuildRendererWithDb(db =>
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                null, "welcome", "fr", 1, "French Subject", "<p>french</p>"));
        });

        var result = await renderer.RenderAsync(_tenantId, "welcome", "wolof", "{}");

        result.Subject.Should().Be("French Subject");
    }

    [Fact]
    public async void Does_not_fall_back_when_fr_is_requested_but_only_en_exists()
    {
        var renderer = BuildRendererWithDb(db =>
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                null, "welcome", "en", 1, "English Only", "<p>en</p>"));
        });

        var result = await renderer.RenderAsync(_tenantId, "welcome", "fr", "{}");

        // fr IS the fallback locale, so requesting it must not hop again — stub output
        result.Subject.Should().Contain("welcome");
        result.Subject.Should().NotBe("English Only");
    }

    [Fact]
    public async void Does_not_fall_back_to_en_for_an_unsupported_locale()
    {
        var renderer = BuildRendererWithDb(db =>
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                null, "welcome", "en", 1, "English Only", "<p>en</p>"));
        });

        var result = await renderer.RenderAsync(_tenantId, "welcome", "wolof", "{}");

        // fr is the only fallback locale — en is not a second chance
        result.Subject.Should().Contain("welcome");
        result.Subject.Should().NotBe("English Only");
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

    // ── locale normalisation ────────────────────────────────────────────────
    // A client converted from a lead had PreferredLanguage = "FR", which the consumer passes
    // through verbatim. Template locales are stored lower-case and the lookup runs in PostgreSQL,
    // where string equality is case-sensitive, so nothing matched — and the "fr" fallback was
    // skipped because its guard compared case-INsensitively. The client received the template's
    // own JSON payload as the message body.

    [Theory]
    [InlineData("FR")]
    [InlineData("Fr")]
    [InlineData("fr-FR")]
    [InlineData("fr_FR")]
    [InlineData("  fr  ")]
    public async void Resolves_a_french_template_however_the_locale_is_spelled(string locale)
    {
        var renderer = BuildRendererWithDb(db =>
            db.EmailTemplates.Add(EmailTemplate.Create(
                null, "client.welcome", "fr", 1,
                "Bienvenue {{ full_name }}", "<p>Votre numéro : {{ client_number }}</p>")));

        var result = await renderer.RenderAsync(
            _tenantId, "client.welcome", locale,
            """{"full_name":"Ambroise BAZIE","client_number":"AG000002-2026-000003"}""");

        result.Subject.Should().Be("Bienvenue Ambroise BAZIE");
        result.HtmlBody.Should().Contain("AG000002-2026-000003");
        result.HtmlBody.Should().NotContain("full_name", "the raw payload must never reach the reader");
    }

    [Theory]
    [InlineData("EN")]
    [InlineData("en-GB")]
    public async void Resolves_an_english_template_however_the_locale_is_spelled(string locale)
    {
        var renderer = BuildRendererWithDb(db =>
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                null, "client.welcome", "fr", 1, "Bienvenue", "<p>fr</p>"));
            db.EmailTemplates.Add(EmailTemplate.Create(
                null, "client.welcome", "en", 1, "Welcome", "<p>en</p>"));
        });

        var result = await renderer.RenderAsync(_tenantId, "client.welcome", locale, "{}");

        result.Subject.Should().Be("Welcome");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("PT-BR")]
    [InlineData("")]
    [InlineData("   ")]
    public async void An_unknown_or_blank_locale_falls_back_to_french(string locale)
    {
        var renderer = BuildRendererWithDb(db =>
            db.EmailTemplates.Add(EmailTemplate.Create(
                null, "client.welcome", "fr", 1, "Bienvenue", "<p>fr</p>")));

        var result = await renderer.RenderAsync(_tenantId, "client.welcome", locale, "{}");

        result.Subject.Should().Be("Bienvenue");
    }

    [Fact]
    public async void A_tenant_template_is_still_preferred_when_the_locale_needs_normalising()
    {
        var renderer = BuildRendererWithDb(db =>
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                null, "client.welcome", "fr", 1, "Platform", "<p>platform</p>"));
            db.EmailTemplates.Add(EmailTemplate.Create(
                _tenantId, "client.welcome", "fr", 1, "Tenant", "<p>tenant</p>"));
        });

        var result = await renderer.RenderAsync(_tenantId, "client.welcome", "FR", "{}");

        result.Subject.Should().Be("Tenant");
    }

    [Fact]
    public async void The_stub_fallback_still_applies_when_the_key_itself_is_unknown()
    {
        // Normalisation must not hide a genuinely missing template.
        var renderer = BuildRendererWithDb(db =>
            db.EmailTemplates.Add(EmailTemplate.Create(
                null, "client.welcome", "fr", 1, "Bienvenue", "<p>fr</p>")));

        var result = await renderer.RenderAsync(_tenantId, "no-such-key", "FR", """{"a":1}""");

        result.Subject.Should().Contain("no-such-key");
        result.HtmlBody.Should().Contain("\"a\":1");
    }
}

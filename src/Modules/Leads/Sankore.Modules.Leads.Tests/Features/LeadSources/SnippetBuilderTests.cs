namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.Snippet;
using Xunit;

/// <summary>
/// The snippet is pasted on a CUSTOMER's site, so every address in it must be absolute and every
/// attribute one the SDK reads (<c>data-sankore-*</c>). It once carried relative paths and
/// <c>data-key</c>/<c>data-container</c>: the script 404'd on the customer's own host and, had it
/// loaded, would have found no key.
/// </summary>
public sealed class SnippetBuilderTests
{
    private const string Api = "https://api.example.com";

    private static LeadSourceConfig Source(ScriptConfig script, Guid? tenantId = null) => LeadSourceConfig.Create(
        tenantId ?? Guid.NewGuid(), "WEB", "Site web", LeadChannelType.WebForm, 0,
        IntegrationMode.EmbeddedScript,
        settings: new EmbeddedScriptSettings { Script = script });

    [Fact]
    public void Existing_form_snippet_uses_the_sdk_attributes_and_absolute_addresses()
    {
        var source = Source(new ScriptConfig { FormSelector = "contact-form" });

        var snippet = SnippetBuilder.Build(source, "/sdk/1.3.0/forms.min.js", "sha384-abc", Api);

        snippet.Html.Should().Contain($"src=\"{Api}/sdk/1.3.0/forms.min.js\"")
            .And.Contain($"data-sankore-key=\"{source.PublicKey}\"")
            .And.Contain($"data-sankore-api=\"{Api}\"")
            .And.Contain("data-sankore-form=\"#contact-form\"")
            .And.NotContain("data-key=")
            .And.NotContain("data-container=")
            .And.NotContain("<div");
        snippet.SdkUrl.Should().Be($"{Api}/sdk/1.3.0/forms.min.js");
    }

    [Fact]
    public void A_typed_css_selector_is_kept_as_is()
    {
        var snippet = SnippetBuilder.Build(
            Source(new ScriptConfig { FormSelector = "form.contact" }), "/sdk/1.3.0/forms.min.js", "sha384-abc", Api);

        snippet.Html.Should().Contain("data-sankore-form=\"form.contact\"");
    }

    [Fact]
    public void Hosted_form_snippet_renders_into_its_container()
    {
        var snippet = SnippetBuilder.Build(
            Source(new ScriptConfig { FormMode = ScriptFormMode.Hosted }), "/sdk/1.3.0/forms.min.js", "sha384-abc", Api);

        snippet.Html.Should().Contain("<div id=\"sankore-form\"></div>")
            .And.Contain("data-sankore-render=\"true\"")
            .And.Contain("data-sankore-container=\"#sankore-form\"");
    }

    [Fact]
    public void Redirect_and_no_prevent_are_carried()
    {
        var snippet = SnippetBuilder.Build(Source(new ScriptConfig
        {
            FormSelector = "#f",
            AfterSubmit = ScriptAfterSubmit.Redirect,
            RedirectUrl = "https://example.com/merci?a=1&b=2",
            PreventDefaultSubmit = false,
        }), "/sdk/1.3.0/forms.min.js", "sha384-abc", Api);

        snippet.Html.Should().Contain("data-sankore-redirect=\"https://example.com/merci?a=1&amp;b=2\"")
            .And.Contain("data-sankore-prevent=\"false\"");
    }

    [Fact]
    public async Task Without_a_public_address_the_snippet_is_refused_rather_than_built_from_the_host()
    {
        var tenantId = Guid.NewGuid();
        using var factory = new Sankore.Modules.Leads.Tests.TestSupport.TestDbContextFactory(tenantId);
        await using var db = factory.CreateContext();
        var source = Source(new ScriptConfig { FormSelector = "f" }, tenantId);
        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync();

        // The endpoint passes no host outside Development: the Host header is the caller's to forge.
        var result = await new GetSnippetHandler(db, Microsoft.Extensions.Options.Options.Create(new SnippetOptions()))
            .Handle(new GetSnippetQuery(source.Id, RequestHost: null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SnippetBuilder.PublicUrlNotConfigured);
    }

    [Fact]
    public void A_configured_address_is_reduced_to_its_origin_and_junk_is_refused()
    {
        SnippetBuilder.ApiBaseUrl(new SnippetOptions { PublicBaseUrl = "https://api.example.com/v1/" }, null)
            .Should().Be("https://api.example.com");
        SnippetBuilder.ApiBaseUrl(new SnippetOptions { PublicBaseUrl = "javascript:alert(1)" }, null)
            .Should().BeEmpty();
    }

    [Fact]
    public void Configured_public_address_wins_over_the_request_host()
    {
        SnippetBuilder.ApiBaseUrl(new SnippetOptions { PublicBaseUrl = "https://api.example.com/" }, "internal:8080")
            .Should().Be("https://api.example.com");
        SnippetBuilder.ApiBaseUrl(new SnippetOptions(), "api-sankore-crm.talisoft.net")
            .Should().Be("https://api-sankore-crm.talisoft.net");
    }
}

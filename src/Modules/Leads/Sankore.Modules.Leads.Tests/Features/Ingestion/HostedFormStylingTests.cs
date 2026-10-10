namespace Sankore.Modules.Leads.Tests.Features.Ingestion;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Xunit;

/// <summary>
/// The two tenant-supplied styling values that leave this system and land in a
/// <c>&lt;style&gt;</c> element on a page the tenant does not own.
///
/// <para>
/// The SDK interpolates <c>accentColor</c> and <c>fontFamily</c> straight into a stylesheet
/// inside the hosted form's shadow root. Nothing between the settings editor and that stylesheet
/// escapes them — settings to JSON response to CSS, verbatim — so a value carrying <c>}</c> would
/// close the rule it sits in and style the host page with whatever follows. Both gates are pinned
/// here: the allow-list itself, and the projection that applies it even to a row written before
/// the validators existed.
/// </para>
/// </summary>
public sealed class HostedFormStylingTests
{
    [Theory]
    [InlineData("#2563eb")]
    [InlineData("#fff")]
    [InlineData("#2563ebcc")]
    [InlineData("rebeccapurple")]
    [InlineData("  #2563eb  ")]
    public void A_plain_css_colour_is_served(string value)
        => Settings(value).AccentColor.Should().Be(value.Trim());

    [Theory]
    [InlineData("#2563eb} body { display: none", "a closed rule reaches the host page")]
    [InlineData("red; background: url(https://evil.ci/x)", "a semicolon starts a new declaration")]
    [InlineData("url(https://evil.ci/x)", "a url() fetches from a third party")]
    [InlineData("var(--anything)", "parentheses are outside the allow-list")]
    [InlineData("#zzzzzz", "not a hex colour")]
    [InlineData("", "blank is absent, not a colour")]
    [InlineData("   ", "whitespace is absent, not a colour")]
    public void Anything_that_is_not_a_colour_is_dropped(string value, string why)
    {
        // Dropped, not escaped: the fallback is the SDK's own default, so the worst case is a
        // form in the wrong colour rather than a form that restyles its host.
        Settings(value).AccentColor.Should().BeNull(why);
    }

    [Theory]
    [InlineData("Inter, system-ui, sans-serif", true)]
    [InlineData("\"Helvetica Neue\", Arial, sans-serif", true)]
    [InlineData("inherit", true)]
    [InlineData("Inter} body { display: none", false)]
    [InlineData("Inter; background: red", false)]
    [InlineData("local(Inter)", false)]
    public void A_font_stack_is_allow_listed_the_same_way(string value, bool accepted)
    {
        var settings = new EmbeddedScriptSettings
        {
            HostedForm = new HostedFormConfig { FontFamily = value },
        };

        if (accepted) settings.FontFamily.Should().Be(value);
        else settings.FontFamily.Should().BeNull();
    }

    [Fact]
    public void No_hosted_form_means_no_styling_to_serve()
    {
        var settings = new EmbeddedScriptSettings();

        settings.AccentColor.Should().BeNull();
        settings.FontFamily.Should().BeNull();
        settings.Theme.Should().BeNull();
    }

    [Fact]
    public void The_validators_and_the_projection_agree_on_what_is_a_colour()
    {
        // The projection silently drops; the validator refuses and names the field. They must
        // accept the same set, or a tenant is told their colour is fine and then sees the
        // default — the failure mode this whole chain of settings bugs was made of.
        CssColor.IsValidColor("#2563eb").Should().BeTrue();
        CssColor.IsValidColor("#2563eb} body {").Should().BeFalse();
        CssColor.IsValidFontFamily("Inter, sans-serif").Should().BeTrue();
        CssColor.IsValidFontFamily("Inter; x: y").Should().BeFalse();
    }

    private static EmbeddedScriptSettings Settings(string accentColor)
        => new() { HostedForm = new HostedFormConfig { AccentColor = accentColor } };
}

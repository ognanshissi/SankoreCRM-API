namespace Sankore.Api.Tests.Infrastructure;

using System.Text.Json;
using FluentAssertions;
using Sankore.Api.Infrastructure;
using Sankore.Modules.Leads.Domain;
using Xunit;

/// <summary>
/// The converter's fallback when a body carries no <c>$mode</c>, through the real HTTP path.
///
/// <para>
/// <b>Why this suite exists.</b> The inference was keyed on the flat properties of schema v2 —
/// <c>allowedOrigins</c>, <c>endpointUrl</c>, <c>allowedIpAddresses</c>. v3 moved every one of
/// them into a nested block, so the list matched nothing a current client sends and every
/// discriminator-less body fell through to <c>Internal</c>: a web-form or provider-API payload
/// would have deserialized into an <c>InternalSettings</c> carrying none of its fields, with no
/// error. Nothing else could catch that — the module's own tests never go through this converter,
/// and the editor always sends <c>$mode</c>, so only a body from somewhere else would have hit it.
/// </para>
/// </summary>
public sealed class SourceSettingsInferenceTests
{
    private static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new SourceSettingsJsonConverter());
        return options;
    }

    private static SourceSettings? Read(string json)
        => JsonSerializer.Deserialize<SourceSettings>(json, Options);

    [Theory]
    // v3 — the blocks the editor writes.
    [InlineData("""{"script": {"allowedOrigins": ["https://imf.ci"]}}""", typeof(EmbeddedScriptSettings))]
    [InlineData("""{"hostedForm": {"fields": []}}""", typeof(EmbeddedScriptSettings))]
    [InlineData("""{"pull": {"baseUrl": "https://api.ci"}}""", typeof(ScheduledPullSettings))]
    [InlineData("""{"allowedIps": ["41.66.0.1"]}""", typeof(ServerWebhookSettings))]
    // v2 — flat, as older rows and older clients still send them.
    [InlineData("""{"allowedOrigins": ["https://imf.ci"]}""", typeof(EmbeddedScriptSettings))]
    [InlineData("""{"endpointUrl": "https://api.ci/leads"}""", typeof(ScheduledPullSettings))]
    [InlineData("""{"allowedIpAddresses": ["41.66.0.1"]}""", typeof(ServerWebhookSettings))]
    [InlineData("""{"signatureAlgorithm": "sha256"}""", typeof(ServerWebhookSettings))]
    [InlineData("""{"trackedKeywords": ["#credit"]}""", typeof(SocialTrackingSettings))]
    [InlineData("""{"platformName": "facebook", "formId": "123"}""", typeof(PlatformSettings))]
    public void A_body_without_a_discriminator_is_inferred_from_its_shape(string json, Type expected)
        => Read(json)!.GetType().Should().Be(expected);

    [Fact]
    public void An_inferred_embedded_script_body_keeps_the_fields_it_was_inferred_from()
    {
        // Resolving the right type is only half of it: the old marker list would also have
        // produced an InternalSettings that silently carried none of this.
        var settings = (EmbeddedScriptSettings)Read("""
        {"script": {"allowedOrigins": ["https://imf.ci"], "captchaProvider": "Turnstile"},
         "hostedForm": {"submitLabel": "Envoyer"}}
        """)!;

        settings.Script!.AllowedOrigins.Should().Equal("https://imf.ci");
        settings.Script.CaptchaProvider.Should().Be(CaptchaProvider.Turnstile);
        settings.HostedForm!.SubmitLabel.Should().Be("Envoyer");
    }

    [Fact]
    public void An_explicit_discriminator_always_wins_over_the_shape()
    {
        // A body that looks like one mode and declares another must be taken at its word; the
        // declaration is what the validators then check against the source's own mode.
        Read("""{"$mode": "Internal", "script": {"allowedOrigins": []}}""")
            .Should().BeOfType<InternalSettings>();
    }

    [Fact]
    public void A_body_with_no_marker_at_all_is_Internal()
        => Read("""{"schemaVersion": 3}""").Should().BeOfType<InternalSettings>();
}

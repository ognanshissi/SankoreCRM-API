namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Xunit;

/// <summary>
/// docs/formulaire-heberge.md lot 2: a script source whose every lead would be refused does not go
/// live. A hosted form without consent text shows no box (the ingest wants consent), and a captcha
/// without its secret key cannot be checked.
/// </summary>
public sealed class HostedFormActivationTests
{
    private static LeadSourceConfig Testing(EmbeddedScriptSettings settings)
    {
        var source = LeadSourceConfig.Create(Guid.NewGuid(), "SITE", "Site", LeadChannelType.WebForm, 0,
            IntegrationMode.EmbeddedScript, settings: settings);
        source.StartTesting();
        return source;
    }

    private static EmbeddedScriptSettings Hosted(string? consentText, CaptchaProvider captcha = CaptchaProvider.None) => new()
    {
        Script = new ScriptConfig { FormMode = ScriptFormMode.Hosted, CaptchaProvider = captcha, CaptchaSiteKey = "site" },
        HostedForm = new HostedFormConfig { ConsentText = consentText },
    };

    [Fact]
    public void A_hosted_form_without_consent_text_cannot_go_live()
    {
        var source = Testing(Hosted(consentText: " "));

        source.Activate().Should().Contain("HostedConsentTextMissing");
        source.Status.Should().Be(LeadSourceStatus.Testing);
    }

    [Fact]
    public void A_captcha_without_its_secret_cannot_go_live()
    {
        Testing(Hosted("J'accepte", CaptchaProvider.Turnstile)).Activate(captchaSecretStored: false)
            .Should().Equal("CaptchaSecretMissing");
    }

    [Fact]
    public void A_complete_hosted_form_goes_live()
    {
        var source = Testing(Hosted("J'accepte", CaptchaProvider.Turnstile));

        source.Activate(captchaSecretStored: true).Should().BeEmpty();
        source.Status.Should().Be(LeadSourceStatus.Active);
    }

    [Fact]
    public void An_existing_form_without_captcha_needs_neither()
    {
        var source = Testing(new EmbeddedScriptSettings { Script = new ScriptConfig { FormSelector = "#f" } });

        source.Activate(captchaSecretStored: false).Should().BeEmpty();
    }
}

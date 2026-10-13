namespace Sankore.Modules.Leads.Features.LeadSources.UpdateLeadSource;

using FluentValidation;
using NCrontab;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.Mapping;
using Sankore.Shared.Infrastructure.Validation;

internal sealed class UpdateLeadSourceValidator
    : AbstractValidator<UpdateLeadSourceCommand>
{
    public UpdateLeadSourceValidator()
    {
        RuleFor(x => x.SourceId).NotEmpty();
        RuleFor(x => x.Label).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.DisplayOrder).GreaterThanOrEqualTo(0)
            .When(x => x.DisplayOrder.HasValue);
        RuleFor(x => x.DedupWindowDays).GreaterThanOrEqualTo(0)
            .When(x => x.DedupWindowDays.HasValue);
        RuleFor(x => x.CostCurrency)
            .MaximumLength(3)
            .When(x => x.CostPerLead.HasValue);

        When(x => x.Settings is EmbeddedScriptSettings, () =>
        {
            // The two values that leave this system and land in a <style> element on somebody
            // else's page. Refused here so the tenant is told, rather than silently dropped by
            // the projection and the form quietly reverting to the SDK's default colour.
            RuleFor(x => ((EmbeddedScriptSettings)x.Settings!).HostedForm!.AccentColor)
                .Must(CssColor.IsValidColor)
                .When(x => !string.IsNullOrWhiteSpace(
                    ((EmbeddedScriptSettings)x.Settings!).HostedForm?.AccentColor))
                .WithClientField("settings.hostedForm.accentColor")
                .WithMessage("settings.hostedForm.accentColor must be a hex colour (#2563eb) or a CSS colour keyword.");

            RuleFor(x => ((EmbeddedScriptSettings)x.Settings!).HostedForm!.FontFamily)
                .Must(CssColor.IsValidFontFamily)
                .When(x => !string.IsNullOrWhiteSpace(
                    ((EmbeddedScriptSettings)x.Settings!).HostedForm?.FontFamily))
                .WithClientField("settings.hostedForm.fontFamily")
                .WithMessage("settings.hostedForm.fontFamily must be a plain font stack, e.g. \"Inter, system-ui, sans-serif\".");

            RuleForEach(x => ((EmbeddedScriptSettings)x.Settings!).AllowedOrigins)
                .Must(BeAValidUrl)
                .WithMessage("settings.script.allowedOrigins[{CollectionIndex}] must be a valid http/https URL.");
        });

        When(x => x.Settings is ServerWebhookSettings, () =>
        {
            RuleFor(x => ((ServerWebhookSettings)x.Settings!).ContentType)
                .NotEmpty().WithClientField("settings.contentType");
            RuleFor(x => ((ServerWebhookSettings)x.Settings!).SignatureAlgorithm)
                .Must(a => a is "sha256" or "sha512")
                .When(x => ((ServerWebhookSettings)x.Settings!).SignatureAlgorithm is not null)
                .WithMessage("settings.signatureAlgorithm must be 'sha256' or 'sha512'.");
        });

        When(x => x.Settings is ScheduledPullSettings, () =>
        {
            // Validated on the COMPOSED url, named after the field the editor owns: the three
            // parts are only ever wrong together, and "settings.endpointUrl" named nothing the
            // caller could go and correct.
            //
            // FORMAT only, and nothing about presence: a pull source is configured over several
            // visits — mappings, consent, then the provider's URL when it arrives — and refusing
            // the save made the whole screen unsaveable until the URL was typed, which is why
            // this rule read as a bug rather than as a requirement. An ABSENT endpoint is refused
            // where it actually matters: `CheckActivationPrerequisites` answers
            // `NoEndpointConfigured` rather than activating, and `LeadSourceConfig.UpdateSettings`
            // refuses to blank the URL of a source that is already past Testing. A present one
            // must still parse — a typo stored now is a pull that fails later, in a Hangfire log.
            RuleFor(x => ((ScheduledPullSettings)x.Settings!).EndpointUrl)
                .Must(BeAValidUrl)
                .When(x => !string.IsNullOrWhiteSpace(
                    ((ScheduledPullSettings)x.Settings!).EndpointUrl))
                .WithClientField("settings.pull.baseUrl")
                .WithMessage("settings.pull.baseUrl and requestPath must compose a valid http/https URL.");

            // No rule on the verb: it is an enum now, so an unknown value is refused when the
            // body is deserialized rather than here.

            // A blank cron is legal — the orchestrator falls back to the record's default — but
            // an UNPARSEABLE one is not, and it fails invisibly: IsCronDue catches the parse
            // error and answers "not due", so the source simply never pulls again.
            RuleFor(x => ((ScheduledPullSettings)x.Settings!).Pull!.CronExpression)
                .Must(BeAParseableCron)
                .When(x => !string.IsNullOrWhiteSpace(
                    ((ScheduledPullSettings)x.Settings!).Pull?.CronExpression))
                .WithClientField("settings.pull.cronExpression")
                .WithMessage("settings.pull.cronExpression is not a valid cron expression.");
        });

        When(x => x.Settings is PlatformSettings, () =>
        {
            RuleFor(x => ((PlatformSettings)x.Settings!).PlatformName)
                .NotEmpty().WithClientField("settings.platformName");
        });

        When(x => x.Settings is SocialTrackingSettings, () =>
        {
            RuleFor(x => ((SocialTrackingSettings)x.Settings!).PlatformName)
                .NotEmpty().WithClientField("settings.platformName");
            RuleFor(x => ((SocialTrackingSettings)x.Settings!).MinEngagementScore)
                .GreaterThanOrEqualTo(0).WithClientField("settings.minEngagementScore");
        });

        // One failure per mapping error, so the client gets a usable list
        // rather than a single joined string.
        RuleFor(x => x.Settings)
            .Custom((settings, ctx) =>
            {
                var rules = settings.FieldMappingsOf();
                if (rules is null or { Count: 0 }) return;

                foreach (var error in FieldMappingEngine.Validate(rules))
                    ctx.AddFailure("settings.fieldMappings", $"{error.Path}: {error.Message}");
            });
    }


    /// <summary>
    /// Five-field cron, as <c>LeadSourcePullOrchestratorJob</c> parses it. Parsed rather than
    /// pattern-matched so this agrees with the scheduler by construction.
    /// </summary>
    private static bool BeAParseableCron(string? expression)
        => CrontabSchedule.TryParse(expression) is not null;

    private static bool BeAValidUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme is "http" or "https";
}

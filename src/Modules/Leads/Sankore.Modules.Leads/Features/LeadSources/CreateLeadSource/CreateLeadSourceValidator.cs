namespace Sankore.Modules.Leads.Features.LeadSources.CreateLeadSource;

using FluentValidation;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.Mapping;

internal sealed class CreateLeadSourceValidator
    : AbstractValidator<CreateLeadSourceCommand>
{
    public CreateLeadSourceValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Label).NotEmpty().MaximumLength(100);
        RuleFor(x => x.ChannelType).IsInEnum();
        RuleFor(x => x.IntegrationMode).IsInEnum()
            .When(x => x.IntegrationMode.HasValue);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.DisplayOrder).GreaterThanOrEqualTo(0);
        RuleFor(x => x.CostCurrency)
            .MaximumLength(3)
            .When(x => x.CostPerLead.HasValue);

        // ── Polymorphic SourceSettings validation ─────────────────────────
        When(x => x.Settings is EmbeddedScriptSettings, () =>
        {
            RuleForEach(x => ((EmbeddedScriptSettings)x.Settings!).AllowedOrigins)
                .Must(BeAValidUrl)
                .WithMessage("settings.allowedOrigins[{CollectionIndex}] must be a valid http/https URL.");
            RuleFor(x => ((EmbeddedScriptSettings)x.Settings!).FormContainerId)
                .MaximumLength(100)
                .WithName("settings.formContainerId");
        });

        When(x => x.Settings is ServerWebhookSettings, () =>
        {
            RuleFor(x => ((ServerWebhookSettings)x.Settings!).ContentType)
                .NotEmpty().WithName("settings.contentType");
            RuleFor(x => ((ServerWebhookSettings)x.Settings!).SignatureAlgorithm)
                .Must(a => a is "sha256" or "sha512")
                .When(x => ((ServerWebhookSettings)x.Settings!).SignatureAlgorithm is not null)
                .WithMessage("settings.signatureAlgorithm must be 'sha256' or 'sha512'.");
        });

        When(x => x.Settings is ScheduledPullSettings, () =>
        {
            RuleFor(x => ((ScheduledPullSettings)x.Settings!).EndpointUrl)
                .NotEmpty()
                .Must(BeAValidUrl)
                .WithName("settings.endpointUrl");
            RuleFor(x => ((ScheduledPullSettings)x.Settings!).HttpMethod)
                .Must(m => m is "GET" or "POST")
                .WithName("settings.httpMethod")
                .WithMessage("settings.httpMethod must be 'GET' or 'POST'.");
            RuleFor(x => ((ScheduledPullSettings)x.Settings!).CronSchedule)
                .NotEmpty()
                .WithName("settings.cronSchedule");
        });

        When(x => x.Settings is PlatformSettings, () =>
        {
            RuleFor(x => ((PlatformSettings)x.Settings!).PlatformName)
                .NotEmpty().WithName("settings.platformName");
        });

        When(x => x.Settings is SocialTrackingSettings, () =>
        {
            RuleFor(x => ((SocialTrackingSettings)x.Settings!).PlatformName)
                .NotEmpty().WithName("settings.platformName");
            RuleFor(x => ((SocialTrackingSettings)x.Settings!).MinEngagementScore)
                .GreaterThanOrEqualTo(0).WithName("settings.minEngagementScore");
        });

        // ── FieldMappings validation (shared across modes) ────────────────
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

    private static bool BeAValidUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme is "http" or "https";
}

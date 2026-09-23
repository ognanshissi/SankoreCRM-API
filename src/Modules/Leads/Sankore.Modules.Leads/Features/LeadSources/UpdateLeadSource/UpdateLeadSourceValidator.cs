namespace Sankore.Modules.Leads.Features.LeadSources.UpdateLeadSource;

using FluentValidation;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.Mapping;

internal sealed class UpdateLeadSourceValidator
    : AbstractValidator<UpdateLeadSourceCommand>
{
    public UpdateLeadSourceValidator()
    {
        RuleFor(x => x.SourceId).NotEmpty();
        RuleFor(x => x.Label).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.DisplayOrder).GreaterThanOrEqualTo(0);
        RuleFor(x => x.DedupWindowDays).GreaterThanOrEqualTo(0)
            .When(x => x.DedupWindowDays.HasValue);
        RuleFor(x => x.CostCurrency)
            .MaximumLength(3)
            .When(x => x.CostPerLead.HasValue);

        When(x => x.Settings is EmbeddedScriptSettings, () =>
        {
            RuleForEach(x => ((EmbeddedScriptSettings)x.Settings!).AllowedOrigins)
                .Must(BeAValidUrl)
                .WithMessage("settings.allowedOrigins[{CollectionIndex}] must be a valid http/https URL.");
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
                .WithName("settings.httpMethod");
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

        When(x => GetFieldMapping(x.Settings) is not null, () =>
        {
            RuleFor(x => GetFieldMapping(x.Settings)!)
                .Must(fm => FieldMappingEngine.Validate(fm).Count == 0)
                .WithMessage(x =>
                {
                    var errors = FieldMappingEngine.Validate(GetFieldMapping(x.Settings)!);
                    return string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}"));
                })
                .WithName("settings.fieldMapping");
        });
    }

    private static IReadOnlyDictionary<string, string>? GetFieldMapping(SourceSettings? s)
        => s switch
        {
            ServerWebhookSettings wh => wh.FieldMapping,
            ScheduledPullSettings pull => pull.FieldMapping,
            PlatformSettings plat => plat.FieldMapping,
            _ => null
        };

    private static bool BeAValidUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme is "http" or "https";
}

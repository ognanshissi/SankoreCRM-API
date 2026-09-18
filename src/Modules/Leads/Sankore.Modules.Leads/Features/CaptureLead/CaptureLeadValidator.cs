using FluentValidation;
using Microsoft.Extensions.Localization;
using Sankore.Modules.Leads.Resources;

namespace Sankore.Modules.Leads.Features.CaptureLead;

public sealed class CaptureLeadValidator : AbstractValidator<CaptureLeadCommand>
{
    public CaptureLeadValidator(IStringLocalizer<LeadsErrors> localizer)
    {
        RuleFor(x => x.TenantId).NotEmpty();

        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage(_ => localizer["Lead.FullName.Required"])
            .MaximumLength(200);

        RuleFor(x => x.PhoneNumber)
            .NotEmpty().WithMessage(_ => localizer["Lead.Phone.Required"])
            .Matches(@"^\+?[0-9\s\-]{8,20}$").WithMessage(_ => localizer["Lead.Phone.Format"]);

        RuleFor(x => x.InterestedProduct).NotEmpty();
        RuleFor(x => x.PreferredLanguage).NotEmpty();

        RuleFor(x => x.DesiredCurrency)
            .NotEmpty().WithMessage("DesiredCurrency is required when DesiredAmount is provided.")
            .Length(3).WithMessage("Currency must be an ISO 4217 code (3 letters).")
            .When(x => x.DesiredAmount.HasValue);

        RuleFor(x => x.Latitude).InclusiveBetween(-90, 90);
        RuleFor(x => x.Longitude).InclusiveBetween(-180, 180);
    }
}
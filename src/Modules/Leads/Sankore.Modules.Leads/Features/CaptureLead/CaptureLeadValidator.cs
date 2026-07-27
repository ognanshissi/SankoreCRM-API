namespace Sankore.Modules.Leads.Features.CaptureLead;

using FluentValidation;

public sealed class CaptureLeadValidator: AbstractValidator<CaptureLeadCommand>
{
    public CaptureLeadValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();

        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage("Full name is required.")
            .MaximumLength(200);

        RuleFor(x => x.PhoneNumber)
            .NotEmpty().WithMessage("Phone number is required.")
            .Matches(@"^\+?[0-9\s\-]{8,20}$").WithMessage("Phone number format is invalid.");

        RuleFor(x => x.InterestedProduct).NotEmpty();
        RuleFor(x => x.PreferredLanguage).NotEmpty();

        RuleFor(x => x.Latitude).InclusiveBetween(-90, 90);
        RuleFor(x => x.Longitude).InclusiveBetween(-180, 180);
    }
}

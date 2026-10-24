namespace Sankore.Modules.Leads.Features.UpdateLead;

using FluentValidation;

internal sealed class UpdateLeadValidator : AbstractValidator<UpdateLeadCommand>
{
    public UpdateLeadValidator()
    {
        RuleFor(x => x.LeadId).NotEmpty();

        When(x => x.FullName is not null, () =>
            RuleFor(x => x.FullName).NotEmpty().MaximumLength(200));

        When(x => x.Email is not null, () =>
            RuleFor(x => x.Email).EmailAddress().MaximumLength(200));

        When(x => x.Latitude.HasValue, () =>
            RuleFor(x => x.Latitude).InclusiveBetween(-90, 90));

        When(x => x.Longitude.HasValue, () =>
            RuleFor(x => x.Longitude).InclusiveBetween(-180, 180));

        RuleFor(x => x.DesiredAmount)
            .GreaterThan(0).When(x => x.DesiredAmount.HasValue);

        // Optional: an amount without one takes the institution's currency (the handler decides).
        RuleFor(x => x.DesiredCurrency)
            .Length(3).WithMessage("Currency must be an ISO 4217 code (3 letters).")
            .When(x => x.DesiredAmount.HasValue && !string.IsNullOrWhiteSpace(x.DesiredCurrency));
    }
}

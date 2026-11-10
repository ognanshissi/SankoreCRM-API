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

        RuleFor(x => x.CompanyName).MaximumLength(200);
        RuleFor(x => x.CompanyPhone).MaximumLength(30);
        RuleFor(x => x.Website).MaximumLength(300);
        When(x => !string.IsNullOrWhiteSpace(x.CompanyEmail), () =>
            RuleFor(x => x.CompanyEmail).EmailAddress().MaximumLength(200));

        RuleFor(x => x.ContactJobTitle).MaximumLength(150);
        RuleFor(x => x.ActivitySector)
            .Must(s => string.IsNullOrWhiteSpace(s) || Domain.LeadActivitySectors.Normalize(s) is not null)
            .WithMessage("Secteur d'activité inconnu.");
        RuleFor(x => x.CompanySize)
            .Must(s => string.IsNullOrWhiteSpace(s) || Domain.LeadCompanySizes.Parse(s) is not null)
            .WithMessage("Effectif illisible : une tranche (Micro, Small, Medium, Large) ou un nombre de personnes.");
        RuleFor(x => x.EstimatedAnnualPurchases)
            .GreaterThan(0).When(x => x.EstimatedAnnualPurchases.HasValue)
            .WithMessage("Le volume d'achat annuel doit être positif.");

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

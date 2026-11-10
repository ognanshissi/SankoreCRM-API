using FluentValidation;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel.ValueObject;
using Microsoft.Extensions.Localization;
using Sankore.Modules.Leads.Resources;

namespace Sankore.Modules.Leads.Features.CaptureLead;

public sealed class CaptureLeadValidator : AbstractValidator<CaptureLeadCommand>
{
    public CaptureLeadValidator(IStringLocalizer<LeadsErrors> localizer)
    {
        RuleFor(x => x.TenantId).NotEmpty();

        RuleFor(x => x.PhoneNumber)
            .NotEmpty().WithMessage(_ => localizer["Lead.Phone.Required"])
            .Matches(@"^\+?[0-9\s\-]{8,20}$").WithMessage(_ => localizer["Lead.Phone.Format"]);

        RuleFor(x => x.InterestedProduct).NotEmpty();

        // Optional: an amount without one takes the institution's currency (the handler decides).
        RuleFor(x => x.DesiredCurrency)
            .Length(3).WithMessage("Currency must be an ISO 4217 code (3 letters).")
            .When(x => x.DesiredAmount.HasValue && !string.IsNullOrWhiteSpace(x.DesiredCurrency));

        // Same bounds as POST leads/{id}/enrich, which writes the same columns.
        RuleFor(x => x.Profession).MaximumLength(150);
        RuleFor(x => x.AddressLine).MaximumLength(300);
        RuleFor(x => x.PostalCode).MaximumLength(20);
        RuleFor(x => x.City).MaximumLength(100);
        RuleFor(x => x.CountryCode)
            .Must(c => string.IsNullOrWhiteSpace(c) || IsoCountry.IsKnown(c))
            .WithMessage("CountryCode must be an ISO 3166-1 alpha-2 country code.");
        RuleFor(x => x.MaritalStatus)
            .Must(m => string.IsNullOrWhiteSpace(m) || Enum.GetNames<LeadMaritalStatus>().Contains(m.Trim(), StringComparer.OrdinalIgnoreCase))
            .WithMessage("MaritalStatus must be one of: " + string.Join(", ", Enum.GetNames<LeadMaritalStatus>()));

        // The company (key accounts lot 1): same bounds as PUT leads/{id}.
        RuleFor(x => x.CompanyName).MaximumLength(200);
        RuleFor(x => x.CompanyPhone).MaximumLength(30);
        RuleFor(x => x.Website).MaximumLength(300);
        RuleFor(x => x.ContactJobTitle).MaximumLength(150);
        RuleFor(x => x.ActivitySector)
            .Must(s => string.IsNullOrWhiteSpace(s) || LeadActivitySectors.Normalize(s) is not null)
            .WithMessage("Secteur d'activité inconnu.");
        RuleFor(x => x.CompanySize)
            .Must(s => string.IsNullOrWhiteSpace(s) || LeadCompanySizes.Parse(s) is not null)
            .WithMessage("Effectif illisible : une tranche (Micro, Small, Medium, Large) ou un nombre de personnes.");
        RuleFor(x => x.EstimatedAnnualPurchases)
            .GreaterThan(0).When(x => x.EstimatedAnnualPurchases.HasValue)
            .WithMessage("Le volume d'achat annuel doit être positif.");

        RuleFor(x => x.Latitude).InclusiveBetween(-90, 90);
        RuleFor(x => x.Longitude).InclusiveBetween(-180, 180);
    }
}
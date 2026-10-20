namespace Sankore.Modules.Leads.Features.Household.SetHouseholdResources;

using FluentValidation;

internal sealed class SetHouseholdResourcesValidator : AbstractValidator<SetHouseholdResourcesCommand>
{
    // A trillion per month is not a household; it is a typo that would make every ratio meaningless.
    private const decimal Ceiling = 1_000_000_000_000m;

    public SetHouseholdResourcesValidator()
    {
        RuleFor(c => c.LeadId).NotEmpty();
        RuleFor(c => c.MonthlyIncome).GreaterThanOrEqualTo(0).LessThan(Ceiling);
        RuleFor(c => c.HousingCost).GreaterThanOrEqualTo(0).LessThan(Ceiling);
        RuleFor(c => c.OtherDebts).GreaterThanOrEqualTo(0).LessThan(Ceiling);
        RuleFor(c => c.Currency)
            .Must(c => c is null || (c.Length == 3 && c.All(char.IsLetter)))
            .WithMessage("Currency must be an ISO 4217 code.");
    }
}

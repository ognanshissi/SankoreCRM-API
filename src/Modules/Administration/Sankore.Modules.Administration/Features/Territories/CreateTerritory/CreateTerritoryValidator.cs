using FluentValidation;

namespace Sankore.Modules.Administration.Features.Territories.CreateTerritory;

public sealed class CreateTerritoryValidator : AbstractValidator<CreateTerritoryCommand>
{
    public CreateTerritoryValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Code).NotEmpty().MaximumLength(50)
            .Matches("^[A-Za-z0-9_-]+$")
            .WithMessage("Code must contain only letters, digits, hyphens, or underscores.");
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.RayonKm).GreaterThanOrEqualTo(0);

        When(x => x.Latitude.HasValue || x.Longitude.HasValue, () =>
        {
            RuleFor(x => x.Latitude)
                .NotNull().WithMessage("Latitude is required when Longitude is provided.")
                .InclusiveBetween(-90, 90);
            RuleFor(x => x.Longitude)
                .NotNull().WithMessage("Longitude is required when Latitude is provided.")
                .InclusiveBetween(-180, 180);
        });
    }
}

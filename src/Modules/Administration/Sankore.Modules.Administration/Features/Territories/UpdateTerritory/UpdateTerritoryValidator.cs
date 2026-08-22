using FluentValidation;

namespace Sankore.Modules.Administration.Features.Territories.UpdateTerritory;

public sealed class UpdateTerritoryValidator : AbstractValidator<UpdateTerritoryCommand>
{
    public UpdateTerritoryValidator()
    {
        RuleFor(x => x.TerritoryId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
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

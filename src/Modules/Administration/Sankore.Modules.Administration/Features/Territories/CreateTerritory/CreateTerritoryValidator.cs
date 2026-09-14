using FluentValidation;
using Microsoft.Extensions.Localization;
using Sankore.Modules.Administration.Resources;

namespace Sankore.Modules.Administration.Features.Territories.CreateTerritory;

public sealed class CreateTerritoryValidator : AbstractValidator<CreateTerritoryCommand>
{
    public CreateTerritoryValidator(IStringLocalizer<AdministrationErrors> localizer)
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Code).NotEmpty().MaximumLength(50)
            .Matches("^[A-Za-z0-9_-]+$")
            .WithMessage(_ => localizer["Territory.Code.Format"]);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.RayonKm).GreaterThanOrEqualTo(0);

        When(x => x.Latitude.HasValue || x.Longitude.HasValue, () =>
        {
            RuleFor(x => x.Latitude)
                .NotNull().WithMessage(_ => localizer["Territory.Latitude.Required"])
                .InclusiveBetween(-90, 90);
            RuleFor(x => x.Longitude)
                .NotNull().WithMessage(_ => localizer["Territory.Longitude.Required"])
                .InclusiveBetween(-180, 180);
        });
    }
}

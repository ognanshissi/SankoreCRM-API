using FluentValidation;
using Microsoft.Extensions.Localization;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Resources;

namespace Sankore.Modules.Administration.Features.Agencies.CreateAgency;

public sealed class CreateAgencyValidator : AbstractValidator<CreateAgencyCommand>
{
    public CreateAgencyValidator(IStringLocalizer<AdministrationErrors> localizer)
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.AgencyType).IsInEnum();

        When(x => x.AgencyType != AgencyType.HeadQuarter, () =>
            RuleFor(x => x.ParentAgencyId)
                .NotEmpty()
                .WithMessage(_ => localizer["Agency.ParentAgencyId.Required"]));

        When(x => x.Latitude.HasValue || x.Longitude.HasValue, () =>
        {
            RuleFor(x => x.Latitude).NotNull().InclusiveBetween(-90, 90);
            RuleFor(x => x.Longitude).NotNull().InclusiveBetween(-180, 180);
        });
    }
}

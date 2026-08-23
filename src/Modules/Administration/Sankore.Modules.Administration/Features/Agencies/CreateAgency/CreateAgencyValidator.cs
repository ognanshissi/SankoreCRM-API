using FluentValidation;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Features.Agencies.CreateAgency;

public sealed class CreateAgencyValidator: AbstractValidator<CreateAgencyCommand>
{
    public CreateAgencyValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.AgencyType).IsInEnum();

        // Non-HQ agencies require a parent
        When(x => x.AgencyType != AgencyType.HeadQuarter, () =>
            RuleFor(x => x.ParentAgencyId)
                .NotEmpty()
                .WithMessage("ParentAgencyId is required for non-headquarters agencies."));

        When(x => x.Latitude.HasValue || x.Longitude.HasValue, () =>
        {
            RuleFor(x => x.Latitude).NotNull().InclusiveBetween(-90, 90);
            RuleFor(x => x.Longitude).NotNull().InclusiveBetween(-180, 180);
        });
    }
}

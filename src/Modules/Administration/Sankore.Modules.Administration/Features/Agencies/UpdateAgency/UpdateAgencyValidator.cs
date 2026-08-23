using FluentValidation;

namespace Sankore.Modules.Administration.Features.Agencies.UpdateAgency;

public sealed class UpdateAgencyValidator : AbstractValidator<UpdateAgencyCommand>
{
    public UpdateAgencyValidator()
    {
        RuleFor(x => x.AgencyId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.AgencyType).IsInEnum();

        When(x => x.Latitude.HasValue || x.Longitude.HasValue, () =>
        {
            RuleFor(x => x.Latitude).NotNull().InclusiveBetween(-90, 90);
            RuleFor(x => x.Longitude).NotNull().InclusiveBetween(-180, 180);
        });
    }
}

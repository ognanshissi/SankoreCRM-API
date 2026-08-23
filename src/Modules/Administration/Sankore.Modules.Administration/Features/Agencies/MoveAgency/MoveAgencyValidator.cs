using FluentValidation;

namespace Sankore.Modules.Administration.Features.Agencies.MoveAgency;

public sealed class MoveAgencyValidator : AbstractValidator<MoveAgencyCommand>
{
    public MoveAgencyValidator()
    {
        RuleFor(x => x.AgencyId).NotEmpty();
    }
}

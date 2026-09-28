using FluentValidation;

namespace Sankore.Modules.Administration.Features.Agencies.AssignAgencyManager;

public sealed class AssignAgencyManagerValidator : AbstractValidator<AssignAgencyManagerCommand>
{
    public AssignAgencyManagerValidator()
    {
        RuleFor(x => x.AgencyId).NotEqual(Guid.Empty);

        // NotEqual(Guid.Empty), not NotEmpty(): on a Guid, FluentValidation's NotEmpty compares
        // against default(Guid) — which IS Guid.Empty — but the intent is clearer spelled out,
        // and it stays correct if the property ever becomes nullable.
        RuleFor(x => x.ManagerUserId).NotEqual(Guid.Empty);
    }
}

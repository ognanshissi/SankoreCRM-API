namespace Sankore.Modules.Leads.Features.Household.AddHouseholdMember;

using FluentValidation;
using Sankore.Shared.Infrastructure.Validation;

internal sealed class AddHouseholdMemberValidator : AbstractValidator<AddHouseholdMemberCommand>
{
    public AddHouseholdMemberValidator()
    {
        RuleFor(c => c.LeadId).NotEmpty();
        RuleFor(c => c.Type).NotEmpty();
        RuleFor(c => c.Identity).NotNull();

        // The SHARED identity rules (BNF-B-02) — name formats, plausible dates, a real ISO
        // country — composed rather than restated, so the principal, a spouse, a child and a
        // beneficiary are all collected to one standard.
        RuleFor(c => c.Identity).SetValidator(new PersonIdentityDtoValidator()!)
            .When(c => c.Identity is not null);

        RuleFor(c => c.Pep!).SetValidator(new PepStatusDtoValidator()!)
            .When(c => c.Pep is not null);
    }
}

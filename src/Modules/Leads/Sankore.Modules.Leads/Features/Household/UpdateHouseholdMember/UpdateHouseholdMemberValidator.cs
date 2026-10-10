namespace Sankore.Modules.Leads.Features.Household.UpdateHouseholdMember;

using FluentValidation;
using Sankore.Shared.Infrastructure.Validation;

internal sealed class UpdateHouseholdMemberValidator : AbstractValidator<UpdateHouseholdMemberCommand>
{
    public UpdateHouseholdMemberValidator()
    {
        RuleFor(c => c.LeadId).NotEmpty();
        RuleFor(c => c.MemberId).NotEmpty();
        RuleFor(c => c.Identity).NotNull();
        RuleFor(c => c.Identity).SetValidator(new PersonIdentityDtoValidator()!)
            .When(c => c.Identity is not null);
    }
}

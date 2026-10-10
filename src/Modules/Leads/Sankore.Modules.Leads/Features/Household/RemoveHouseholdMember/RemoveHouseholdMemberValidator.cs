namespace Sankore.Modules.Leads.Features.Household.RemoveHouseholdMember;

using FluentValidation;

internal sealed class RemoveHouseholdMemberValidator : AbstractValidator<RemoveHouseholdMemberCommand>
{
    public RemoveHouseholdMemberValidator()
    {
        RuleFor(c => c.LeadId).NotEmpty();
        RuleFor(c => c.MemberId).NotEmpty();
        RuleFor(c => c.Reason).NotEmpty().MinimumLength(3).MaximumLength(500);
    }
}

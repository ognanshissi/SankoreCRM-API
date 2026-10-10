namespace Sankore.Modules.Leads.Features.Household.SetPepStatus;

using FluentValidation;
using Sankore.Shared.Infrastructure.Validation;

internal sealed class SetPrincipalPepStatusValidator : AbstractValidator<SetPrincipalPepStatusCommand>
{
    public SetPrincipalPepStatusValidator()
    {
        RuleFor(c => c.LeadId).NotEmpty();
        RuleFor(c => c.Pep).NotNull();
        RuleFor(c => c.Pep).SetValidator(new PepStatusDtoValidator()!)
            .When(c => c.Pep is not null);
    }
}

internal sealed class SetHouseholdMemberPepStatusValidator
    : AbstractValidator<SetHouseholdMemberPepStatusCommand>
{
    public SetHouseholdMemberPepStatusValidator()
    {
        RuleFor(c => c.LeadId).NotEmpty();
        RuleFor(c => c.MemberId).NotEmpty();
        RuleFor(c => c.Pep).NotNull();
        RuleFor(c => c.Pep).SetValidator(new PepStatusDtoValidator()!)
            .When(c => c.Pep is not null);

        // BNF-B-05 allows an agent to depart from the FamilyMember link the principal's status
        // propagates — "modifiable avec motif". The reason is what makes that reviewable, so a
        // member-level override states one.
        RuleFor(c => c.Pep.OverrideReason)
            .NotEmpty().WithMessage(
                "A reason is required when classifying a household member's exposure individually.")
            .MinimumLength(3)
            .When(c => c.Pep is { IsPep: true });
    }
}

namespace Sankore.Modules.Customers.Features.Lifecycle.AssignAdvisor;

using FluentValidation;

public sealed class AssignAdvisorValidator : AbstractValidator<AssignAdvisorCommand>
{
    public AssignAdvisorValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty();

        // An explicit null clears the advisor; an all-zero Guid is a caller bug,
        // not an intention, and would otherwise be looked up as a real user id.
        // NotEqual, not NotEmpty: on a Guid? FluentValidation's NotEmpty compares
        // against default(Guid?) — i.e. null — and lets Guid.Empty straight through.
        When(x => x.AdvisorUserId.HasValue,
            () => RuleFor(x => x.AdvisorUserId).NotEqual(Guid.Empty));
    }
}

namespace Sankore.Modules.Customers.Features.Duplicates.RejectDuplicateCandidate;

using FluentValidation;

public sealed class RejectDuplicateCandidateValidator : AbstractValidator<RejectDuplicateCandidateCommand>
{
    public RejectDuplicateCandidateValidator()
    {
        RuleFor(x => x.CandidateId).NotEmpty();

        // The reason is the only trace of WHY a pair was dismissed; a one-word "no" is worthless
        // to the next reviewer, hence the floor.
        RuleFor(x => x.Reason)
            .NotEmpty()
            .MinimumLength(5)
            .MaximumLength(500);
    }
}

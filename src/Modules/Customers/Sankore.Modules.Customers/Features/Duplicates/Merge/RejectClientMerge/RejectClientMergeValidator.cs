namespace Sankore.Modules.Customers.Features.Duplicates.Merge.RejectClientMerge;

using FluentValidation;

public sealed class RejectClientMergeValidator : AbstractValidator<RejectClientMergeCommand>
{
    public RejectClientMergeValidator()
    {
        RuleFor(x => x.MergeRequestId).NotEmpty();

        RuleFor(x => x.Reason)
            .NotEmpty()
            .MinimumLength(5)
            .MaximumLength(1000);
    }
}

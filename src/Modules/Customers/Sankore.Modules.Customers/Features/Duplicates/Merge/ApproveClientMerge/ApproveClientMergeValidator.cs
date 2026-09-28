namespace Sankore.Modules.Customers.Features.Duplicates.Merge.ApproveClientMerge;

using FluentValidation;

public sealed class ApproveClientMergeValidator : AbstractValidator<ApproveClientMergeCommand>
{
    public ApproveClientMergeValidator()
    {
        RuleFor(x => x.MergeRequestId).NotEmpty();

        // The comment is optional on an approval (it is mandatory on a rejection): approving means
        // agreeing with the reason already recorded by the requester.
        RuleFor(x => x.Comment).MaximumLength(1000);
    }
}

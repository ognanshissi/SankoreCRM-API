namespace Sankore.Modules.Leads.Features.Bulk.BulkAssignOwner;

using FluentValidation;

internal sealed class BulkAssignOwnerValidator : AbstractValidator<BulkAssignOwnerCommand>
{
    public BulkAssignOwnerValidator()
    {
        RuleFor(x => x.LeadIds)
            .NotEmpty().WithMessage("At least one lead id is required.")
            .Must(ids => ids.Count <= 100).WithMessage("Maximum 100 leads per bulk operation.");

        RuleFor(x => x.OwnerId).NotEmpty();
    }
}

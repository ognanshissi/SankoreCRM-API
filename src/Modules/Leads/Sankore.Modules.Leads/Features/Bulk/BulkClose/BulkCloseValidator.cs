namespace Sankore.Modules.Leads.Features.Bulk.BulkClose;

using FluentValidation;

internal sealed class BulkCloseValidator : AbstractValidator<BulkCloseCommand>
{
    public BulkCloseValidator()
    {
        RuleFor(x => x.LeadIds)
            .NotEmpty().WithMessage("At least one lead id is required.")
            .Must(ids => ids.Count <= 100).WithMessage("Maximum 100 leads per bulk operation.");
    }
}

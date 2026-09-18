namespace Sankore.Modules.Leads.Features.Bulk.BulkRecycle;

using FluentValidation;

internal sealed class BulkRecycleValidator : AbstractValidator<BulkRecycleCommand>
{
    public BulkRecycleValidator()
    {
        RuleFor(x => x.LeadIds)
            .NotEmpty().WithMessage("At least one lead id is required.")
            .Must(ids => ids.Count <= 100).WithMessage("Maximum 100 leads per bulk operation.");

        RuleFor(x => x.NewCampaign)
            .MaximumLength(100)
            .When(x => x.NewCampaign is not null);
    }
}

namespace Sankore.Modules.Leads.Features.Bulk.BulkTag;

using FluentValidation;

internal sealed class BulkTagValidator : AbstractValidator<BulkTagCommand>
{
    public BulkTagValidator()
    {
        RuleFor(x => x.LeadIds)
            .NotEmpty().WithMessage("At least one lead id is required.")
            .Must(ids => ids.Count <= 100).WithMessage("Maximum 100 leads per bulk operation.");

        RuleFor(x => x.Tag)
            .NotEmpty()
            .MaximumLength(50)
            .Matches(@"^[a-zA-Z0-9\-_]+$")
            .WithMessage("Tag must contain only letters, numbers, hyphens and underscores.");

        RuleFor(x => x.AddedBy).NotEmpty();
    }
}

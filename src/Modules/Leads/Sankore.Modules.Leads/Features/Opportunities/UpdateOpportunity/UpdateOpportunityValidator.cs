namespace Sankore.Modules.Leads.Features.Opportunities.UpdateOpportunity;

using FluentValidation;

/// <summary>The column limits, so an over-long value is a 400 that says which field, not a 500.</summary>
internal sealed class UpdateOpportunityValidator : AbstractValidator<UpdateOpportunityCommand>
{
    public UpdateOpportunityValidator()
    {
        RuleFor(x => x.OpportunityId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200).When(x => x.Title is not null);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.Product).MaximumLength(100);
        RuleFor(x => x.NextStep).MaximumLength(200);
        RuleFor(x => x.EstimatedAmount).GreaterThan(0).When(x => x.EstimatedAmount.HasValue);
    }
}

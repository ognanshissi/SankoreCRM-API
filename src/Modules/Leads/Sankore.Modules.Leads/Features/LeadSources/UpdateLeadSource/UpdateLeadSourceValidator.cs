namespace Sankore.Modules.Leads.Features.LeadSources.UpdateLeadSource;

using FluentValidation;

internal sealed class UpdateLeadSourceValidator
    : AbstractValidator<UpdateLeadSourceCommand>
{
    public UpdateLeadSourceValidator()
    {
        RuleFor(x => x.SourceId).NotEmpty();
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Label).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.DisplayOrder).GreaterThanOrEqualTo(0);
    }
}

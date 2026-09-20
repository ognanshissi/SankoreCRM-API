namespace Sankore.Modules.Leads.Features.LeadSources.CreateLeadSource;

using FluentValidation;

internal sealed class CreateLeadSourceValidator
    : AbstractValidator<CreateLeadSourceCommand>
{
    public CreateLeadSourceValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Label).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.DisplayOrder).GreaterThanOrEqualTo(0);
    }
}

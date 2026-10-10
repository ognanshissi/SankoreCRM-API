namespace Sankore.Modules.Leads.Features.CustomerLink.DetachLeadFromCustomer;

using FluentValidation;

internal sealed class DetachLeadFromCustomerValidator
    : AbstractValidator<DetachLeadFromCustomerCommand>
{
    public DetachLeadFromCustomerValidator()
    {
        RuleFor(c => c.LeadId).NotEmpty();
        RuleFor(c => c.Reason).NotEmpty().MinimumLength(3).MaximumLength(500);
    }
}

namespace Sankore.Modules.Leads.Features.CustomerLink.AttachLeadToCustomer;

using FluentValidation;

internal sealed class AttachLeadToCustomerValidator : AbstractValidator<AttachLeadToCustomerCommand>
{
    public AttachLeadToCustomerValidator()
    {
        RuleFor(c => c.LeadId).NotEmpty();
        RuleFor(c => c.CustomerId).NotEmpty();

        // A reason long enough to mean something, short enough to store. The aggregate refuses a
        // blank one too (CUSTOMER_LINK_REASON_REQUIRED) — this is the field-level 400 that tells
        // the agent WHICH field is wrong.
        RuleFor(c => c.Reason).NotEmpty().MinimumLength(3).MaximumLength(500);
    }
}

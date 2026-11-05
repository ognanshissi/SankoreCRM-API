namespace Sankore.Modules.Leads.Features.RecycleLead;

using FluentValidation;

internal sealed class RecycleLeadValidator : AbstractValidator<RecycleLeadCommand>
{
    public RecycleLeadValidator()
    {
        RuleFor(x => x.LeadId).NotEmpty();
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

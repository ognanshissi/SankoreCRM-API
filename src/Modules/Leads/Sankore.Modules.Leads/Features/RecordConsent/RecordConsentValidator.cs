namespace Sankore.Modules.Leads.Features.RecordConsent;

using FluentValidation;

internal sealed class RecordConsentValidator : AbstractValidator<RecordConsentCommand>
{
    public RecordConsentValidator()
    {
        RuleFor(x => x.LeadId).NotEmpty();
        RuleFor(x => x.RecordedBy).NotEmpty();
        RuleFor(x => x.ProofReference).MaximumLength(1000).When(x => x.ProofReference is not null);
    }
}

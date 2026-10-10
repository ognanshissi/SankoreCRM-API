namespace Sankore.Modules.Leads.Features.Questionnaires.RecordHealthConsent;

using FluentValidation;
using Sankore.Modules.Leads.Domain;

internal sealed class RecordHealthConsentValidator : AbstractValidator<RecordHealthConsentCommand>
{
    public RecordHealthConsentValidator()
    {
        RuleFor(c => c.LeadId).NotEmpty();
        RuleFor(c => c.SubjectId).NotEmpty();
        RuleFor(c => c.SubjectType).NotEmpty();

        RuleFor(c => c.Channel)
            .NotEmpty()
            .Must(ch => Enum.TryParse<ConsentChannel>(ch, ignoreCase: true, out var parsed)
                        && Enum.IsDefined(parsed))
            .WithMessage($"'{{PropertyName}}' must be one of: {string.Join(", ", Enum.GetNames<ConsentChannel>())}.");

        // Required, unlike the generic consent endpoint where it is optional. A health consent is
        // the one this module has to be able to evidence.
        RuleFor(c => c.ProofReference)
            .NotEmpty().WithMessage("Evidence of a health-data consent is required (BNF-B-09).")
            .MaximumLength(1000);
    }
}

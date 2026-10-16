namespace Sankore.Modules.Integration.Features.Telephony.WrapUp;

using FluentValidation;

internal sealed class WrapUpCallValidator : AbstractValidator<WrapUpCallCommand>
{
    public WrapUpCallValidator()
    {
        RuleFor(x => x.CallId).NotEmpty().OverridePropertyName("callId");

        RuleFor(x => x.OutcomeCode)
            .NotEmpty()
            .MaximumLength(30)
            .OverridePropertyName("outcomeCode");

        // 2000 and not the 500 of a timeline summary: this is what an agent actually typed, kept
        // whole, and it never reaches a summary column — the projection of B-11 carries the
        // outcome's LABEL and not the note. See TelephonyCallWrappedUpEvent.
        RuleFor(x => x.Note)
            .MaximumLength(2000)
            .When(x => x.Note is not null)
            .OverridePropertyName("note");

        // No rule on CallbackAt being in the future: an agent wrapping up at 18:05 a call in which
        // "tomorrow morning" was agreed may well enter a time that has passed by the time they
        // press save, and refusing it would be a validation nobody can satisfy twice.
    }
}

namespace Sankore.Modules.Integration.Features.Reconciliation.ResolveGap;

using FluentValidation;

/// <summary>
/// Shape only. Whether the gap exists, belongs to this tenant or is still open is the handler's
/// and the aggregate's answer, not a validation rule — a validator that read the database would
/// answer 422 for a gap of another tenant and thereby confirm it exists.
///
/// <para>
/// <c>OverridePropertyName</c> and never <c>WithName</c>: the 422 body is keyed by the JSON
/// property the caller sent, and <c>WithName</c> changes only the human message while leaving the
/// key as the C# member — the front then cannot attach the error to its field.
/// </para>
/// </summary>
internal sealed class ResolveReconciliationGapValidator
    : AbstractValidator<ResolveReconciliationGapCommand>
{
    public ResolveReconciliationGapValidator()
    {
        RuleFor(x => x.GapId).NotEmpty().OverridePropertyName("gapId");

        // Mandatory, and refused here rather than left to the aggregate so the caller is told
        // WHICH field is missing. The aggregate still enforces it — see IntegrationReconciliationGap
        // .Resolve — because this validator only guards the HTTP path.
        RuleFor(x => x.Note)
            .NotEmpty()
            .OverridePropertyName("note")
            .WithMessage(
                "A resolution note is required: a compliance finding closed without a reason is "
                + "the one thing this ledger exists to prevent.")
            .MaximumLength(1000)
            .OverridePropertyName("note")
            .WithMessage("A resolution note cannot exceed 1000 characters.");
    }
}

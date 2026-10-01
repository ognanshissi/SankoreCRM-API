namespace Sankore.Modules.Kyc.Features.Duplicates.ClearDuplicateFlag;

using FluentValidation;

internal sealed class ClearDuplicateFlagValidator : AbstractValidator<ClearDuplicateFlagCommand>
{
    /// <summary>Long enough to be a sentence, short enough not to become a case note.</summary>
    internal const int MinimumReasonLength = 10;
    internal const int MaximumReasonLength = 500;

    public ClearDuplicateFlagValidator()
    {
        RuleFor(x => x.KycFileId).NotEmpty();

        RuleFor(x => x.Reason)
            .NotEmpty()
            .MinimumLength(MinimumReasonLength)
            .MaximumLength(MaximumReasonLength)
            .WithMessage("Un motif explicite est requis pour lever une suspicion de doublon.");

        // Mandatory, unlike on an automatic trigger: lifting a compliance flag is always somebody's
        // decision. The handler re-checks the reason on its own, so the rule still holds for a
        // caller that reaches it without the validation pipeline.
        RuleFor(x => x.ClearedBy).NotEmpty();
    }
}

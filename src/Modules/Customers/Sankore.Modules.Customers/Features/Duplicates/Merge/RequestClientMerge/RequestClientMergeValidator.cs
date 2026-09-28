namespace Sankore.Modules.Customers.Features.Duplicates.Merge.RequestClientMerge;

using FluentValidation;

public sealed class RequestClientMergeValidator : AbstractValidator<RequestClientMergeCommand>
{
    public RequestClientMergeValidator()
    {
        RuleFor(x => x.SurvivorClientId).NotEmpty();
        RuleFor(x => x.AbsorbedClientId).NotEmpty();

        // Merging is irreversible: the approver needs to read why it was asked for.
        RuleFor(x => x.Reason)
            .NotEmpty()
            .MinimumLength(10)
            .MaximumLength(500);

        // Unknown FIELD names are ignored silently by the executor, but a choice value that is
        // neither "survivor" nor "absorbed" is a caller bug worth reporting.
        RuleForEach(x => x.FieldChoices)
            .Must(pair => string.Equals(pair.Value, ClientMergeFields.Survivor, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(pair.Value, ClientMergeFields.Absorbed, StringComparison.OrdinalIgnoreCase))
            .WithMessage($"Each field choice must be '{ClientMergeFields.Survivor}' or '{ClientMergeFields.Absorbed}'.");
    }
}

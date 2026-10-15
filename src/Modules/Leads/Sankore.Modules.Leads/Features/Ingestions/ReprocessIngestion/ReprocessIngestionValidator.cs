namespace Sankore.Modules.Leads.Features.Ingestions.ReprocessIngestion;

using FluentValidation;
using Sankore.Modules.Leads.Features.Ingestion;

internal sealed class ReprocessIngestionValidator : AbstractValidator<ReprocessIngestionCommand>
{
    public ReprocessIngestionValidator()
    {
        RuleFor(x => x.IngestionId).NotEmpty();
        RuleFor(x => x.FieldLinks).NotNull();

        // Only the fields the lead is built from: a link to any other target would be stored
        // nowhere and the lead created without the value the operator thought they gave.
        RuleForEach(x => x.FieldLinks)
            .Must(link => InboundLeadFactory.CapturedFields.Contains(link.Key))
            .WithMessage((_, link) => $"Unknown target field: '{link.Key}'.")
            .Must(link => !string.IsNullOrWhiteSpace(link.Value))
            .WithMessage((_, link) => $"A payload key is required for '{link.Key}'.");
    }
}

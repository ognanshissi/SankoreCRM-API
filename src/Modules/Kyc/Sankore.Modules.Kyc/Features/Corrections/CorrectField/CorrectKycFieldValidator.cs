namespace Sankore.Modules.Kyc.Features.Corrections.CorrectField;

using FluentValidation;

internal sealed class CorrectKycFieldValidator : AbstractValidator<CorrectKycFieldCommand>
{
    public CorrectKycFieldValidator()
    {
        RuleFor(x => x.KycFileId).NotEmpty();

        // 100 is the column width of kyc_field_corrections.field_name: a longer name would be
        // truncated by PostgreSQL and the trail would point at a field nobody can find again.
        RuleFor(x => x.FieldName).NotEmpty().MaximumLength(100);

        RuleFor(x => x.Source)
            .Must(KycCorrectionSources.IsKnown)
            .WithMessage("La source d'une correction est OCR ou MRZ.");

        // No MaximumLength on NewValue: it is encrypted into an unbounded column, and a cap here
        // would silently refuse a long address that the document genuinely carries.
        RuleFor(x => x.NewValue).NotEmpty();

        // CorrectedBy is required here, unlike CreateKycFileCommand: a correction has no automatic
        // trigger. Every one of them is a human overriding a machine, and an unattributed
        // override is exactly what the compliance review is looking for.
        RuleFor(x => x.CorrectedBy).NotEmpty();
    }
}

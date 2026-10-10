namespace Sankore.Modules.Leads.Features.Fatca.SubmitDeclaration;

using FluentValidation;
using Sankore.Modules.Leads.Domain;

internal sealed class SubmitFatcaDeclarationValidator
    : AbstractValidator<SubmitFatcaDeclarationCommand>
{
    public SubmitFatcaDeclarationValidator()
    {
        RuleFor(c => c.LeadId).NotEmpty();
        RuleFor(c => c.SubjectId).NotEmpty();
        RuleFor(c => c.SubjectType).NotEmpty();
        RuleFor(c => c.Indicia).NotNull();

        RuleFor(c => c.Documentation)
            .NotEmpty()
            .Must(d => Enum.TryParse<FatcaDocumentation>(d, ignoreCase: true, out var parsed)
                       && Enum.IsDefined(parsed))
            .WithMessage($"'{{PropertyName}}' must be one of: {string.Join(", ", Enum.GetNames<FatcaDocumentation>())}.");

        // A US TIN is nine digits, optionally hyphenated as an SSN or an EIN. Checked for SHAPE
        // only: whether the IRS issued it is not knowable from here, and a shape check is the
        // honest limit — but it does catch the common case of a national id typed into the wrong
        // field, which would otherwise be filed as a reportable identifier.
        RuleFor(c => c.UsTin)
            .Matches(@"^\d{3}-?\d{2}-?\d{4}$|^\d{2}-?\d{7}$")
            .WithMessage("'{PropertyName}' must be a 9-digit US TIN (SSN or EIN format).")
            .When(c => !string.IsNullOrWhiteSpace(c.UsTin));

        RuleFor(c => c.SelfCertificationDocRef).MaximumLength(500);
        RuleFor(c => c.EvidenceDocRef).MaximumLength(500);
        RuleFor(c => c.RefusalReason).MaximumLength(1000);
    }
}

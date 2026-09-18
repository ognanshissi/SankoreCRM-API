namespace Sankore.Modules.Leads.Features.Import;

using FluentValidation;

internal sealed class ImportLeadsValidator : AbstractValidator<ImportLeadsCommand>
{
    public ImportLeadsValidator()
    {
        RuleFor(x => x.Rows)
            .NotEmpty().WithMessage("At least one row is required.")
            .Must(rows => rows.Count <= 500).WithMessage("Maximum 500 rows per import batch.");

        RuleForEach(x => x.Rows).ChildRules(row =>
        {
            row.RuleFor(r => r.FullName).NotEmpty();
            row.RuleFor(r => r.PhoneNumber).NotEmpty();
            row.RuleFor(r => r.InterestedProduct).NotEmpty();
            row.RuleFor(r => r.PreferredLanguage).NotEmpty();
        });
    }
}

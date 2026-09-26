namespace Sankore.Modules.Leads.Features.Import.ImportFromGoogleSheet;

using FluentValidation;

public sealed class ImportFromGoogleSheetValidator : AbstractValidator<ImportFromGoogleSheetCommand>
{
    public ImportFromGoogleSheetValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.InitiatedBy).NotEmpty();
        RuleFor(x => x.SpreadsheetUrl).NotEmpty();
    }
}

namespace Sankore.Modules.Leads.Features.Import.ImportFromFile;

using FluentValidation;

public sealed class ImportFromFileValidator : AbstractValidator<ImportFromFileCommand>
{
    public ImportFromFileValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.InitiatedBy).NotEmpty();
        RuleFor(x => x.FileReference).NotEmpty();
        RuleFor(x => x.OriginalFileName).NotEmpty();
    }
}

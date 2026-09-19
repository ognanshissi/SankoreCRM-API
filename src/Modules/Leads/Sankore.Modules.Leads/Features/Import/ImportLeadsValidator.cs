namespace Sankore.Modules.Leads.Features.Import;

using FluentValidation;

internal sealed class ImportLeadsValidator : AbstractValidator<ImportLeadsCommand>
{
    public ImportLeadsValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.InitiatedBy).NotEmpty();
        RuleFor(x => x.FileReference).NotEmpty();
        RuleFor(x => x.OriginalFileName).NotEmpty();
    }
}

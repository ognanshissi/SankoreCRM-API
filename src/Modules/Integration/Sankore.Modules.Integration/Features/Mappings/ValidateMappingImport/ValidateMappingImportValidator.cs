namespace Sankore.Modules.Integration.Features.Mappings.ValidateMappingImport;

using FluentValidation;

internal sealed class ValidateMappingImportValidator : AbstractValidator<ValidateMappingImportCommand>
{
    public ValidateMappingImportValidator()
    {
        RuleFor(c => c.ConnectionId).NotEmpty();
        RuleFor(c => c.FileReference).NotEmpty();
    }
}

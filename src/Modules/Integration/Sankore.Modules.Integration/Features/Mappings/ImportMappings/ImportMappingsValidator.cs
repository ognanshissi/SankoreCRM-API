namespace Sankore.Modules.Integration.Features.Mappings.ImportMappings;

using FluentValidation;

internal sealed class ImportMappingsValidator : AbstractValidator<ImportMappingsCommand>
{
    public ImportMappingsValidator()
    {
        RuleFor(c => c.ConnectionId).NotEmpty();
        RuleFor(c => c.FileReference).NotEmpty();
    }
}

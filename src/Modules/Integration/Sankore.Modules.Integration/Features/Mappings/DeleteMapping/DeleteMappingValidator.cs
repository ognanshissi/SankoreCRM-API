namespace Sankore.Modules.Integration.Features.Mappings.DeleteMapping;

using FluentValidation;

internal sealed class DeleteMappingValidator : AbstractValidator<DeleteMappingCommand>
{
    public DeleteMappingValidator()
    {
        RuleFor(c => c.ConnectionId).NotEmpty();
        RuleFor(c => c.CrmCode).NotEmpty().MaximumLength(100);
    }
}

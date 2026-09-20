namespace Sankore.Modules.Leads.Features.TaskTypes.UpdateTaskType;

using FluentValidation;

internal sealed class UpdateTaskTypeValidator : AbstractValidator<UpdateTaskTypeCommand>
{
    public UpdateTaskTypeValidator()
    {
        RuleFor(x => x.TaskTypeId).NotEmpty();
        RuleFor(x => x.Label).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.DisplayOrder).GreaterThanOrEqualTo(0);
    }
}

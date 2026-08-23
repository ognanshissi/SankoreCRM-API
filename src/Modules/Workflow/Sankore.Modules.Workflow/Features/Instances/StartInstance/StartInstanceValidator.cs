using FluentValidation;

namespace Sankore.Modules.Workflow.Features.Instances.StartInstance;

public sealed class StartInstanceValidator : AbstractValidator<StartInstanceCommand>
{
    public StartInstanceValidator()
    {
        RuleFor(x => x.EntityType).NotEmpty().MaximumLength(100);
        RuleFor(x => x.EntityId).NotEmpty();
    }
}

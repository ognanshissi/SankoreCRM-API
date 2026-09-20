namespace Sankore.Modules.Leads.Features.PipelineStages.CreatePipelineStage;

using FluentValidation;

internal sealed class CreatePipelineStageValidator : AbstractValidator<CreatePipelineStageCommand>
{
    public CreatePipelineStageValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Label).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Color).MaximumLength(7);
        RuleFor(x => x.DisplayOrder).GreaterThanOrEqualTo(0);
    }
}

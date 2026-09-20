namespace Sankore.Modules.Leads.Features.PipelineStages.UpdatePipelineStage;

using FluentValidation;

internal sealed class UpdatePipelineStageConfigValidator : AbstractValidator<UpdatePipelineStageConfigCommand>
{
    public UpdatePipelineStageConfigValidator()
    {
        RuleFor(x => x.StageId).NotEmpty();
        RuleFor(x => x.Label).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Color).MaximumLength(7);
        RuleFor(x => x.DisplayOrder).GreaterThanOrEqualTo(0);
    }
}

namespace Sankore.Modules.Leads.Features.TaskGenerationRules.CreateTaskGenerationRule;

using FluentValidation;
using Sankore.Modules.Leads.Domain;

internal sealed class CreateTaskGenerationRuleValidator
    : AbstractValidator<CreateTaskGenerationRuleCommand>
{
    public CreateTaskGenerationRuleValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.TriggerEventType).NotEmpty()
            .Must(t => t == TaskTriggerEvents.LeadDispatched
                    || t == TaskTriggerEvents.LeadDispatchingFailed
                    || t == TaskTriggerEvents.LeadScoreCriticallyChanged)
            .WithMessage("TriggerEventType must be one of the known event types.");
        RuleFor(x => x.TitleTemplate).NotEmpty().MaximumLength(200);
        RuleFor(x => x.SlaDuration).GreaterThan(TimeSpan.Zero);
        RuleFor(x => x.DueDuration).GreaterThan(TimeSpan.Zero);
    }
}

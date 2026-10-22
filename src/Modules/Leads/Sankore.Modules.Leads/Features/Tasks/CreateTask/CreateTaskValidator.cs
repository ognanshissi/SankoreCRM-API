namespace Sankore.Modules.Leads.Features.Tasks.CreateTask;

using FluentValidation;
using Sankore.Modules.Leads.Domain;

internal sealed class CreateTaskValidator : AbstractValidator<CreateTaskCommand>
{
    public CreateTaskValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(CrmTask.TitleMaxLength);
        RuleFor(x => x.Description).MaximumLength(CrmTask.DescriptionMaxLength);
        RuleFor(x => x.DueAt).GreaterThan(DateTimeOffset.UtcNow);
        RuleFor(x => x.ParticipantIds).Must(p => p is null || p.Count <= 20).WithMessage("20 personnes associées au plus.");
        RuleFor(x => x.Criteria).Must(c => c is null || c.Count <= CrmTaskCriterion.MaxPerTask)
            .WithMessage($"{CrmTaskCriterion.MaxPerTask} critères au plus.");
        RuleForEach(x => x.Criteria).MaximumLength(CrmTaskCriterion.LabelMaxLength);
        RuleFor(x => x.Tags).Must(t => t is null || t.Count <= CrmTaskTag.MaxPerTask).WithMessage($"{CrmTaskTag.MaxPerTask} étiquettes au plus.");
        RuleForEach(x => x.Tags).MaximumLength(CrmTaskTag.MaxLength);
    }
}

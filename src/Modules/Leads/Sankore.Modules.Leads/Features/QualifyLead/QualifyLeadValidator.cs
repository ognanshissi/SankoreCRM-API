namespace Sankore.Modules.Leads.Features.QualifyLead;

using FluentValidation;

internal sealed class QualifyLeadValidator : AbstractValidator<QualifyLeadCommand>
{
    public QualifyLeadValidator()
    {
        RuleFor(x => x.LeadId).NotEmpty();
        RuleFor(x => x.TriggerEvent).NotEmpty().MaximumLength(100);

        // QualifiedBy is only ever read on the template path, where it becomes
        // QualificationResponse.AnsweredBy — the other two paths (explicit score override,
        // attribute-based calculation) never touch it.
        //
        // It used to be required unconditionally, which broke auto-dispatch outright:
        // LeadAutoDispatchConsumer has no human actor, so it passes Guid.Empty — the SYSTEM
        // placeholder BackgroundJobContext.SetScope establishes for exactly this case — and the
        // command was rejected before reaching the handler. Every captured lead faulted in the
        // consumer with "'Qualified By' must not be empty" while the HTTP path stayed green,
        // because validation behaviours are registered by the bootstrapper and the end-to-end
        // test builds its pipeline without them.
        //
        // Requiring it where it is consumed keeps the guard meaningful for a human
        // qualification — a template answer must say who answered — without demanding an actor
        // from a caller that legitimately has none. Note that `Answers` is already NotNull when
        // TemplateId is set, so this condition is exactly the handler's Path 1.
        When(x => x.TemplateId.HasValue, () =>
            RuleFor(x => x.QualifiedBy)
                .NotEmpty()
                .WithMessage("QualifiedBy is required when answers are recorded against a template."));

        When(x => x.Score.HasValue, () =>
            RuleFor(x => x.Score!.Value)
                .InclusiveBetween(0, 100)
                .WithMessage("Score must be between 0 and 100."));

        When(x => x.TemplateId.HasValue, () =>
            RuleFor(x => x.Answers)
                .NotNull()
                .WithMessage("Answers are required when a template is specified."));

        When(x => x.Answers is not null, () =>
            RuleForEach(x => x.Answers!).ChildRules(answer =>
            {
                answer.RuleFor(a => a.QuestionId).NotEmpty();
                answer.RuleFor(a => a.Value).NotNull().MaximumLength(500);
            }));
    }
}

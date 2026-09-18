namespace Sankore.Modules.Leads.Features.Reminders.CreateReminder;

using FluentValidation;

internal sealed class CreateReminderValidator : AbstractValidator<CreateReminderCommand>
{
    public CreateReminderValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.CreatedBy).NotEmpty();
        RuleFor(x => x.DueAt).GreaterThan(DateTimeOffset.UtcNow)
            .WithMessage("Due date must be in the future.");
        RuleFor(x => x.Notes).MaximumLength(1000).When(x => x.Notes is not null);
    }
}

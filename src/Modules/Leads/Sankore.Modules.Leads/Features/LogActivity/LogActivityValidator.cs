namespace Sankore.Modules.Leads.Features.LogActivity;

using FluentValidation;

internal sealed class LogActivityValidator : AbstractValidator<LogActivityCommand>
{
    public LogActivityValidator()
    {
        RuleFor(x => x.Subject)
            .NotEmpty().WithMessage("Subject is required.")
            .MaximumLength(200);

        RuleFor(x => x.PerformedBy)
            .NotEmpty().WithMessage("PerformedBy must be a valid user id.");

        RuleFor(x => x.DurationMinutes)
            .GreaterThan(0)
            .When(x => x.DurationMinutes.HasValue)
            .WithMessage("Duration must be greater than zero.");

        RuleFor(x => x.Notes)
            .MaximumLength(2000)
            .When(x => x.Notes is not null);
    }
}

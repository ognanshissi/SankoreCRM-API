namespace Sankore.Modules.Leads.Features.LogActivity;

using FluentValidation;
using Sankore.Modules.Leads.Domain;

internal sealed class LogActivityValidator : AbstractValidator<LogActivityCommand>
{
    public LogActivityValidator()
    {
        RuleFor(x => x.Subject)
            .NotEmpty().WithMessage("Subject is required.")
            .MaximumLength(200);

        RuleFor(x => x.DurationMinutes)
            .GreaterThan(0)
            .When(x => x.DurationMinutes.HasValue)
            .WithMessage("Duration must be greater than zero.");

        RuleFor(x => x.Notes)
            .MaximumLength(2000)
            .When(x => x.Notes is not null);

        RuleFor(x => x.CtiCallReference)
            .MaximumLength(200)
            .When(x => x.CtiCallReference is not null);

        RuleFor(x => x.VisitPhotoReference)
            .MaximumLength(500)
            .When(x => x.VisitPhotoReference is not null);

        // Visit location: both lat/lng must be provided together
        RuleFor(x => x.VisitLongitude)
            .NotNull().WithMessage("VisitLongitude is required when VisitLatitude is provided.")
            .When(x => x.VisitLatitude.HasValue);

        RuleFor(x => x.VisitLatitude)
            .NotNull().WithMessage("VisitLatitude is required when VisitLongitude is provided.")
            .When(x => x.VisitLongitude.HasValue);
    }
}

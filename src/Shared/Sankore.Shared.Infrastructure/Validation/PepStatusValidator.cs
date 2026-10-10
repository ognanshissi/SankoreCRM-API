namespace Sankore.Shared.Infrastructure.Validation;

using FluentValidation;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// The shared rules for a PEP declaration (BNF-B-02, BNF-B-05). Composed by every command that
/// collects one, for the principal as for each household member.
/// </summary>
public sealed class PepStatusValidator : AbstractValidator<PepStatus>
{
    public PepStatusValidator(TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        RuleFor(p => p.Function)
            .NotEmpty().WithMessage("The exposed function is required when a person is declared PEP.")
            .MaximumLength(150)
            .When(p => p.IsPep);

        RuleFor(p => p.Link)
            .NotNull().WithMessage("The nature of the link to the exposed function is required.")
            .When(p => p.IsPep);

        RuleFor(p => p.CountryCode)
            .Must(IsoCountry.IsKnown)
            .WithMessage("'{PropertyName}' must be an ISO 3166-1 alpha-2 country code.")
            .When(p => !string.IsNullOrWhiteSpace(p.CountryCode));

        // A mandate that has not ended yet is a null date, not a future one. Allowing a future
        // date would start the post-mandate countdown from a day that has not happened, and the
        // person would read as exposed for the window PLUS the gap.
        RuleFor(p => p.FunctionEndDate!.Value)
            .LessThanOrEqualTo(today).WithMessage("A function end date cannot be in the future.")
            .When(p => p.FunctionEndDate.HasValue);

        // Nothing about a person who is not exposed: a form that posts a function, a country or an
        // end date alongside IsPep = false has lost track of which person it is describing.
        RuleFor(p => p)
            .Must(p => p.Function is null
                       && p.Link is null
                       && p.FunctionEndDate is null
                       && p.CountryCode is null)
            .WithMessage("A person who is not declared PEP must carry no exposure detail.")
            .When(p => !p.IsPep);

        RuleFor(p => p.OverrideReason)
            .MaximumLength(300);
    }
}

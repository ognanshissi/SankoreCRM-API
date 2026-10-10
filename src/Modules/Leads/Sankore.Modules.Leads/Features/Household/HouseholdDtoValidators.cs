namespace Sankore.Shared.Infrastructure.Validation;

using FluentValidation;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// The shared identity rules of BNF-B-02, applied to the HTTP shape.
///
/// <para>
/// A thin adapter over <see cref="PersonIdentityValidator"/>'s intent rather than a second set of
/// rules: the Kernel validator works on <c>PersonIdentity</c>, which a request cannot be bound to
/// (private constructor, factory that throws). So the DTO is checked here with the SAME
/// constraints, and <c>HouseholdMapping</c> then builds the value object — which re-enforces the
/// invariants by throwing. Two layers, one standard.
/// </para>
/// </summary>
public sealed class PersonIdentityDtoValidator : AbstractValidator<PersonIdentityDto>
{
    private const string NamePattern = @"^[\p{L}\p{M}][\p{L}\p{M}\s'’\-\.]*$";

    public PersonIdentityDtoValidator(TimeProvider? clock = null)
    {
        var today = DateOnly.FromDateTime((clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);

        RuleFor(p => p.LastName)
            .NotEmpty().MaximumLength(100)
            .Matches(NamePattern).WithMessage("'{PropertyName}' contains characters that are not part of a name.");

        RuleFor(p => p.FirstName)
            .NotEmpty().MaximumLength(100)
            .Matches(NamePattern).WithMessage("'{PropertyName}' contains characters that are not part of a name.");

        RuleFor(p => p.UsageName)
            .MaximumLength(100).Matches(NamePattern)
            .When(p => !string.IsNullOrWhiteSpace(p.UsageName));

        RuleFor(p => p.BirthDate!.Value)
            .LessThanOrEqualTo(today).WithMessage("A birth date cannot be in the future.")
            .GreaterThan(new DateOnly(1900, 1, 1)).WithMessage("A birth date before 1900 is not plausible.")
            .When(p => p.BirthDate.HasValue);

        RuleFor(p => p.AddressLine).MaximumLength(200);
        RuleFor(p => p.PostalCode).MaximumLength(10);
        RuleFor(p => p.City).MaximumLength(100);

        RuleFor(p => p.CountryCode)
            .Must(IsoCountry.IsKnown)
            .WithMessage("'{PropertyName}' must be an ISO 3166-1 alpha-2 country code.")
            .When(p => !string.IsNullOrWhiteSpace(p.CountryCode));

        RuleFor(p => p.Gender)
            .Must(g => Enum.TryParse<PersonGender>(g, ignoreCase: true, out var parsed)
                       && Enum.IsDefined(parsed))
            .WithMessage($"'{{PropertyName}}' must be one of: {string.Join(", ", Enum.GetNames<PersonGender>())}.")
            .When(p => !string.IsNullOrWhiteSpace(p.Gender));
    }
}

/// <summary>The shared PEP rules of BNF-B-02/05, applied to the HTTP shape.</summary>
public sealed class PepStatusDtoValidator : AbstractValidator<PepStatusDto>
{
    public PepStatusDtoValidator(TimeProvider? clock = null)
    {
        var today = DateOnly.FromDateTime((clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);

        RuleFor(p => p.Function)
            .NotEmpty().WithMessage("The exposed function is required when a person is declared PEP.")
            .MaximumLength(150)
            .When(p => p.IsPep);

        RuleFor(p => p.Link)
            .NotEmpty().WithMessage("The nature of the link to the exposed function is required.")
            .Must(l => Enum.TryParse<PepLink>(l, ignoreCase: true, out var parsed)
                       && Enum.IsDefined(parsed))
            .WithMessage($"'{{PropertyName}}' must be one of: {string.Join(", ", Enum.GetNames<PepLink>())}.")
            .When(p => p.IsPep);

        RuleFor(p => p.CountryCode)
            .Must(IsoCountry.IsKnown)
            .WithMessage("'{PropertyName}' must be an ISO 3166-1 alpha-2 country code.")
            .When(p => !string.IsNullOrWhiteSpace(p.CountryCode));

        // A mandate that has not ended is a NULL date, not a future one: a future date would start
        // the post-mandate countdown from a day that has not happened, so the person would read as
        // exposed for the window PLUS the gap.
        RuleFor(p => p.FunctionEndDate!.Value)
            .LessThanOrEqualTo(today).WithMessage("A function end date cannot be in the future.")
            .When(p => p.FunctionEndDate.HasValue);

        RuleFor(p => p.OverrideReason).MaximumLength(300);

        RuleFor(p => p)
            .Must(p => p.Function is null && p.Link is null
                    && p.FunctionEndDate is null && p.CountryCode is null)
            .WithMessage("A person who is not declared PEP must carry no exposure detail.")
            .When(p => !p.IsPep);
    }
}

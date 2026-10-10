namespace Sankore.Shared.Infrastructure.Validation;

using FluentValidation;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// The shared rules for a person's identity (BNF-B-02). Every command that collects a principal,
/// a spouse, a child or a beneficiary composes this validator with
/// <c>RuleFor(x => x.Identity).SetValidator(new PersonIdentityValidator())</c> instead of restating
/// the formats — four modules collecting the same person to four different standards is the failure
/// this exists to prevent.
///
/// <para>
/// It lives in Shared.Infrastructure and not next to the value object because the Kernel has
/// <b>zero third-party dependencies</b> by design, FluentValidation included. The invariants that
/// must hold for the type to exist at all are in <see cref="PersonIdentity.Create"/> and throw;
/// these are the request-shaped rules that are worth a 400.
/// </para>
/// </summary>
public sealed class PersonIdentityValidator : AbstractValidator<PersonIdentity>
{
    /// <summary>Names of a natural person: no digits, no control characters.</summary>
    private const string NamePattern = @"^[\p{L}\p{M}][\p{L}\p{M}\s'’\-\.]*$";

    public PersonIdentityValidator(TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        RuleFor(p => p.LastName)
            .NotEmpty()
            .MaximumLength(100)
            .Matches(NamePattern).WithMessage("'{PropertyName}' contains characters that are not part of a name.");

        RuleFor(p => p.FirstName)
            .NotEmpty()
            .MaximumLength(100)
            .Matches(NamePattern).WithMessage("'{PropertyName}' contains characters that are not part of a name.");

        RuleFor(p => p.UsageName)
            .MaximumLength(100)
            .Matches(NamePattern)
            .When(p => !string.IsNullOrWhiteSpace(p.UsageName));

        // Two separate rules rather than an InclusiveBetween: the two failures mean different
        // things to the agent reading the message, and a future date is almost always a typo in
        // the year while 1900 is almost always a placeholder.
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

        // BNF-B-21 is phase 2. Until it lands nothing may write this block: accepting an
        // unverified NIR now would put it into a blind index the dedup then trusts.
        RuleFor(p => p.FrenchSocialSecurity)
            .Must(b => b is null || b.IsEmpty)
            .WithMessage("French social-security identifiers are not collected yet (BNF-B-21, phase 2).");
    }
}

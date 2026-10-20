namespace Sankore.Modules.Leads.Features.Enrichment.EnrichLead;

using FluentValidation;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// Request-shaped rules only (BNF-B-02). Whether the country exists is checked here, because a
/// value object must not fail on a country the host's globalization data happens not to carry —
/// see <c>IsoCountry</c>'s two methods.
/// </summary>
internal sealed class EnrichLeadValidator : AbstractValidator<EnrichLeadCommand>
{
    private static readonly string[] Genders =
        [.. Enum.GetNames<Sankore.Modules.Leads.Domain.LeadGender>()];

    public EnrichLeadValidator()
    {
        RuleFor(x => x.LeadId).NotEmpty();

        RuleFor(x => x.FirstName).MaximumLength(100);
        RuleFor(x => x.LastName).MaximumLength(100);
        RuleFor(x => x.UsageName).MaximumLength(100);
        RuleFor(x => x.AddressLine).MaximumLength(300);
        RuleFor(x => x.PostalCode).MaximumLength(20);
        RuleFor(x => x.City).MaximumLength(100);
        RuleFor(x => x.Neighbourhood).MaximumLength(100);
        RuleFor(x => x.Landmark).MaximumLength(200);

        RuleFor(x => x.Gender)
            .Must(g => g is null || Genders.Contains(g, StringComparer.OrdinalIgnoreCase))
            .WithMessage("Gender must be one of: " + string.Join(", ", Genders));

        RuleFor(x => x.CountryCode)
            .Must(c => c is null || c.Length == 0 || IsoCountry.IsKnown(c))
            .WithMessage("CountryCode must be an ISO 3166-1 alpha-2 country code.");

        // A birth date in the future is a typo, not a prospect. No lower bound: a 110-year-old
        // walks into a branch rarely but legitimately, and refusing the record is worse than
        // holding an improbable one.
        RuleFor(x => x.BirthDate)
            .Must(d => d is null || d <= DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("BirthDate cannot be in the future.");

        // Naming a line AND asking to clear it are two different intentions in one request, and
        // guessing which wins is how a lead silently loses its product line.
        RuleFor(x => x)
            .Must(x => !(x.ClearProductLine && x.ProductLineId is not null))
            .WithMessage("Pass either productLineId or clearProductLine, not both.");
    }
}

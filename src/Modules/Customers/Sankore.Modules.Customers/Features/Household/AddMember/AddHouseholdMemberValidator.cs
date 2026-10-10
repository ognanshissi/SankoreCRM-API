namespace Sankore.Modules.Customers.Features.Household.AddMember;

using FluentValidation;

/// <summary>
/// Request-shaped rules only (BNF-B-13). The type/union-type pairing and the one-spouse invariant
/// are the aggregate's and the index's — a validator cannot see the household.
/// </summary>
internal sealed class AddHouseholdMemberValidator : AbstractValidator<AddHouseholdMemberCommand>
{
    public AddHouseholdMemberValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty();
        RuleFor(x => x.Type).NotEmpty();

        // Exactly one source of identity. Both would leave the handler choosing silently, and a
        // caller who sent both believes the one it is about to discard.
        RuleFor(x => x)
            .Must(x => (x.RelatedClientId is null || x.RelatedClientId == Guid.Empty)
                       ^ (x.Identity is null))
            .WithMessage("Supply either relatedClientId or identity, not both and not neither.");

        When(x => x.Identity is not null, () =>
        {
            RuleFor(x => x.Identity!.LastName).NotEmpty().MaximumLength(100);
            RuleFor(x => x.Identity!.FirstName).NotEmpty().MaximumLength(100);
            RuleFor(x => x.Identity!.UsageName).MaximumLength(100);
            RuleFor(x => x.Identity!.AddressLine).MaximumLength(300);
            RuleFor(x => x.Identity!.PostalCode).MaximumLength(20);
            RuleFor(x => x.Identity!.City).MaximumLength(100);

            RuleFor(x => x.Identity!.BirthDate)
                .Must(d => d is null || d <= DateOnly.FromDateTime(DateTime.UtcNow))
                .WithMessage("BirthDate cannot be in the future.");
        });

        RuleFor(x => x.Pep!.Function).MaximumLength(150).When(x => x.Pep is not null);
        RuleFor(x => x.Pep!.OverrideReason).MaximumLength(300).When(x => x.Pep is not null);
    }
}

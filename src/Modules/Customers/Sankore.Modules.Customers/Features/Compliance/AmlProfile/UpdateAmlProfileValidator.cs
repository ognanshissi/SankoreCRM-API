namespace Sankore.Modules.Customers.Features.Compliance.AmlProfile;

using FluentValidation;
using Sankore.Modules.Customers.Domain;

internal sealed class UpdateAmlProfileValidator : AbstractValidator<UpdateAmlProfileCommand>
{
    public UpdateAmlProfileValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty();

        RuleFor(x => x.ActivitySectorCode).MaximumLength(30);
        RuleFor(x => x.ActivitySectorDetail).MaximumLength(500);
        RuleFor(x => x.CorporatePurpose).MaximumLength(1000);

        // The closed list, refused at the write. A typo would otherwise be a value nothing can
        // group by — and the whole reason the code exists next to the narrative is that it is
        // countable.
        RuleFor(x => x.FundsOriginCode)
            .Must(FundsOriginCodes.IsValid)
            .WithMessage($"FundsOriginCode must be one of: {string.Join(", ", FundsOriginCodes.All)}.");

        // A document cannot have been issued tomorrow. Checked here rather than in the aggregate
        // because it is a request-shaped rule: an existing row with a future date (imported, say)
        // must not become unsaveable.
        RuleFor(x => x.RegistrationExtractIssuedOn)
            .Must(d => d!.Value <= DateOnly.FromDateTime(DateTime.UtcNow))
            .When(x => x.RegistrationExtractIssuedOn.HasValue)
            .WithMessage("The registry extract cannot have been issued in the future.");
    }
}

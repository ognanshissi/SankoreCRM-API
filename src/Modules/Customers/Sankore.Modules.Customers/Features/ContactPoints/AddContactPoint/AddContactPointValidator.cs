using FluentValidation;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Features.ContactPoints.AddContactPoint;

public sealed class AddContactPointValidator : AbstractValidator<AddContactPointCommand>
{
    public AddContactPointValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty();
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.Value).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Label).MaximumLength(100);

        When(x => x.Type == ContactPointType.Email, () =>
            RuleFor(x => x.Value).EmailAddress());

        // Deliberately loose: West-African numbers are entered with or without the
        // calling code, with spaces, dots or dashes. Normalisation happens in the
        // blind indexer, so only the digit count is checked here.
        When(x => x.Type == ContactPointType.Phone, () =>
            RuleFor(x => x.Value)
                .Must(v => v is not null && v.Count(char.IsAsciiDigit) is >= 8 and <= 15)
                .WithMessage("A phone number must contain between 8 and 15 digits."));

        When(x => x.Type == ContactPointType.Address, () =>
            RuleFor(x => x.Value).MinimumLength(5));
    }
}

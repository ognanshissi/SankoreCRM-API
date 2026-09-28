using FluentValidation;

namespace Sankore.Modules.Customers.Features.Relationships.AddRelationship;

public sealed class AddRelationshipValidator : AbstractValidator<AddRelationshipCommand>
{
    public AddRelationshipValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty();
        RuleFor(x => x.Type).IsInEnum();

        // A relationship points at exactly one party: another client of the tenant,
        // or a named person who is not a client. Never both, never neither.
        RuleFor(x => x)
            .Must(c => (c.RelatedClientId is not null && c.RelatedClientId != Guid.Empty)
                       ^ !string.IsNullOrWhiteSpace(c.ExternalFullName))
            .WithName("relatedParty")
            .WithMessage(
                "Provide either relatedClientId (an existing client) or externalFullName " +
                "(a person who is not a client), but not both.");

        RuleFor(x => x.ExternalFullName).MaximumLength(150);
        RuleFor(x => x.ExternalDocumentNumber).MaximumLength(100);

        When(x => !string.IsNullOrWhiteSpace(x.ExternalPhoneNumber), () =>
            RuleFor(x => x.ExternalPhoneNumber)
                .Must(v => v is not null && v.Count(char.IsAsciiDigit) is >= 8 and <= 15)
                .WithMessage("A phone number must contain between 8 and 15 digits."));

        // Identity details only make sense for an external party: a related client
        // already carries its own, and duplicating them here would fork the truth.
        When(x => x.RelatedClientId is not null && x.RelatedClientId != Guid.Empty, () =>
        {
            RuleFor(x => x.ExternalPhoneNumber).Empty();
            RuleFor(x => x.ExternalDateOfBirth).Null();
            RuleFor(x => x.ExternalDocumentNumber).Empty();
        });

        RuleFor(x => x.ExternalDateOfBirth)
            .LessThan(_ => DateOnly.FromDateTime(DateTime.UtcNow))
            .When(x => x.ExternalDateOfBirth is not null);
    }
}

namespace Sankore.Modules.Customers.Features.Clients.CreateIndividualClient;

using FluentValidation;

/// <summary>
/// Shape-level validation only. The business rules that need the database or a tenant
/// setting (minimum age, duplicate document, duplicate phone, agency perimeter) live in
/// the handler and return a <c>CustomerErrors</c> code — a validator cannot express them
/// without reaching outside the request.
///
/// Maximum lengths mirror <c>ClientConfiguration</c>, so an over-long value is rejected
/// with a 400 here rather than by a PostgreSQL truncation error at SaveChanges.
/// </summary>
public sealed class CreateIndividualClientValidator : AbstractValidator<CreateIndividualClientCommand>
{
    public CreateIndividualClientValidator()
    {
        RuleFor(x => x.AgencyId).NotEmpty();

        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(150);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(150);
        RuleFor(x => x.MaidenName).MaximumLength(150);
        RuleFor(x => x.Gender).IsInEnum();

        // A future date of birth is a typo, not a business case. The MINIMUM age is a
        // tenant setting and is therefore checked by the handler, not here.
        RuleFor(x => x.DateOfBirth)
            .NotEqual(default(DateOnly))
            .LessThan(_ => DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime))
            .WithMessage("Date of birth must be in the past.");

        RuleFor(x => x.BirthPlace).MaximumLength(150);
        RuleFor(x => x.Nationality).NotEmpty().MaximumLength(100);
        RuleFor(x => x.MaritalStatus).IsInEnum().When(x => x.MaritalStatus.HasValue);
        RuleFor(x => x.FatherName).MaximumLength(150);
        RuleFor(x => x.MotherName).MaximumLength(150);
        RuleFor(x => x.Profession).MaximumLength(150);
        RuleFor(x => x.Employer).MaximumLength(150);

        RuleFor(x => x.DeclaredIncome)
            .GreaterThanOrEqualTo(0)
            .When(x => x.DeclaredIncome.HasValue);

        // ISO 4217 alphabetic code (XOF, EUR...) — 3 characters, no more.
        RuleFor(x => x.DeclaredIncomeCurrency)
            .Length(3)
            .When(x => !string.IsNullOrWhiteSpace(x.DeclaredIncomeCurrency));

        RuleFor(x => x.DeclaredIncomeCurrency)
            .NotEmpty()
            .When(x => x.DeclaredIncome.HasValue)
            .WithMessage("A declared income requires its currency.");

        RuleFor(x => x.PreferredLanguage).MaximumLength(10);

        RuleFor(x => x.IdentityDocumentType).IsInEnum();
        RuleFor(x => x.IdentityDocumentNumber).NotEmpty().MaximumLength(60);

        RuleFor(x => x.IdentityDocumentExpiresOn)
            .GreaterThan(x => x.IdentityDocumentIssuedOn!.Value)
            .When(x => x.IdentityDocumentIssuedOn.HasValue && x.IdentityDocumentExpiresOn.HasValue)
            .WithMessage("The document expiry date must follow its issue date.");

        // At least one reachable phone: without it the client cannot be contacted, no
        // SMS can be sent, and duplicate detection has nothing to compare.
        RuleFor(x => x.PhoneNumbers)
            .NotNull()
            .Must(p => p is not null && p.Any(n => !string.IsNullOrWhiteSpace(n)))
            .WithMessage("At least one phone number is required.");

        RuleForEach(x => x.PhoneNumbers)
            .MaximumLength(40)
            .When(x => x.PhoneNumbers is not null);

        RuleFor(x => x.Email)
            .EmailAddress()
            .MaximumLength(256)
            .When(x => !string.IsNullOrWhiteSpace(x.Email));

        When(x => x.Address is not null, () =>
        {
            RuleFor(x => x.Address!.Street).MaximumLength(300);
            RuleFor(x => x.Address!.City).MaximumLength(150);
            RuleFor(x => x.Address!.State).MaximumLength(150);
            RuleFor(x => x.Address!.Country).MaximumLength(100);
            RuleFor(x => x.Address!.ZipCode).MaximumLength(30);
        });
    }
}

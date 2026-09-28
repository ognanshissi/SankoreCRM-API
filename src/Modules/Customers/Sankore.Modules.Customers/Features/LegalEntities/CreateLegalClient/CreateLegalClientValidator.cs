namespace Sankore.Modules.Customers.Features.LegalEntities.CreateLegalClient;

using FluentValidation;

/// <summary>
/// Acceptance criteria of US-M01-BE-17: a legal client cannot be created without a
/// company name, a legal form, an RCCM, an incorporation date, a head-office address
/// and AT LEAST ONE contact (a phone number or an e-mail address).
/// </summary>
public sealed class CreateLegalClientValidator : AbstractValidator<CreateLegalClientCommand>
{
    public CreateLegalClientValidator()
    {
        RuleFor(x => x.AgencyId).NotEmpty();

        RuleFor(x => x.LegalName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.LegalFormCode).NotEmpty().MaximumLength(30);

        // The RCCM is the natural key of a company in the OHADA area: mandatory, and
        // the only field of this command carrying a blind index.
        RuleFor(x => x.RegistrationNumber).NotEmpty().MaximumLength(60);
        RuleFor(x => x.TaxIdNumber).MaximumLength(60);

        RuleFor(x => x.IncorporationDate)
            .NotEqual(default(DateOnly))
            .WithMessage("The incorporation date is required.");
        RuleFor(x => x.IncorporationDate)
            .LessThanOrEqualTo(_ => DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("The incorporation date cannot be in the future.");

        // Head office: an address object whose city AND street are both blank is not an address.
        RuleFor(x => x.HeadOfficeAddress)
            .NotNull()
            .WithMessage("The head-office address is required.");
        RuleFor(x => x.HeadOfficeAddress)
            .Must(a => a is not null
                       && (!string.IsNullOrWhiteSpace(a.Street) || !string.IsNullOrWhiteSpace(a.City)))
            .WithMessage("The head-office address must carry at least a street or a city.");

        // At least one contact — phone or e-mail.
        RuleFor(x => x)
            .Must(cmd => (cmd.PhoneNumbers is not null
                          && cmd.PhoneNumbers.Any(p => !string.IsNullOrWhiteSpace(p)))
                         || !string.IsNullOrWhiteSpace(cmd.Email))
            .WithMessage("At least one contact (phone number or e-mail address) is required.")
            .OverridePropertyName(nameof(CreateLegalClientCommand.PhoneNumbers));

        RuleForEach(x => x.PhoneNumbers).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Email).EmailAddress().MaximumLength(200)
            .When(x => !string.IsNullOrWhiteSpace(x.Email));

        RuleFor(x => x.PreferredLanguage).MaximumLength(10);
    }
}

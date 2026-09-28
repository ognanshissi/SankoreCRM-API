namespace Sankore.Modules.Customers.Features.LeadConversion.CreateClientFromLead;

using System.Globalization;
using FluentValidation;

/// <summary>
/// Guards the module boundary: this command does not arrive from an HTTP endpoint that
/// ASP.NET Core has already bound and validated, it arrives from another module's code.
/// The rules below therefore check the things a caller can plausibly get wrong — a missing
/// tenant, a lead with no usable identity, an over-long value that would be truncated by the
/// column — and nothing else. Anything the aggregate itself enforces (client number, agency
/// code) is deliberately not duplicated here.
///
/// <para>
/// Nothing sensitive is dereferenced: the validator never looks at the document number, the
/// phone or the e-mail beyond a length and shape check, so a validation failure message can
/// never leak a protected value.
/// </para>
/// </summary>
internal sealed class CreateClientFromLeadValidator : AbstractValidator<CreateClientFromLeadCommand>
{
    public CreateClientFromLeadValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.LeadId).NotEmpty();
        RuleFor(x => x.AgencyId).NotEmpty();
        RuleFor(x => x.ConvertedByUserId).NotEmpty();

        // An individual needs BOTH names (the aggregate rejects a half-filled pair), a legal
        // entity needs its legal name. Anything else is an incomplete lead.
        RuleFor(x => x)
            .Must(HasUsableIdentity)
            .WithErrorCode(LeadConversionErrorCodes.LeadIdentityIncomplete)
            .WithMessage(
                "The lead must carry either both a first and a last name, or a legal name.")
            .OverridePropertyName(nameof(CreateClientFromLeadCommand.LegalName));

        RuleFor(x => x.FirstName).MaximumLength(100);
        RuleFor(x => x.LastName).MaximumLength(100);
        RuleFor(x => x.LegalName).MaximumLength(250);
        RuleFor(x => x.Nationality).MaximumLength(100);
        RuleFor(x => x.Profession).MaximumLength(150);
        RuleFor(x => x.PreferredLanguage).MaximumLength(10);
        RuleFor(x => x.PhoneNumber).MaximumLength(40);
        RuleFor(x => x.Email).MaximumLength(320);
        RuleFor(x => x.IdentityDocumentNumber).MaximumLength(100);

        // Shape only — a malformed address is the lead's problem, not a reason to refuse the
        // conversion of an otherwise complete client, so this stays a soft format check.
        When(x => !string.IsNullOrWhiteSpace(x.Email), () =>
            RuleFor(x => x.Email).EmailAddress());

        // The birth date travels as an ISO-8601 string (see the command for why). An unparsable
        // value would be silently dropped by the handler, which is worse than refusing it, and a
        // birth date in the future is a data-entry error rather than a client.
        When(x => !string.IsNullOrWhiteSpace(x.DateOfBirth), () =>
        {
            RuleFor(x => x.DateOfBirth)
                .Must(value => Parse(value) is not null)
                .WithMessage(
                    $"The date of birth must be formatted as {CreateClientFromLeadCommand.DateOfBirthFormat}.");

            RuleFor(x => x.DateOfBirth)
                .Must(value => Parse(value) is not { } parsed
                               || parsed <= DateOnly.FromDateTime(DateTime.UtcNow))
                .WithMessage("The date of birth cannot be in the future.");
        });
    }

    private static bool HasUsableIdentity(CreateClientFromLeadCommand cmd)
        => (!string.IsNullOrWhiteSpace(cmd.FirstName) && !string.IsNullOrWhiteSpace(cmd.LastName))
           || !string.IsNullOrWhiteSpace(cmd.LegalName);

    private static DateOnly? Parse(string? value)
        => DateOnly.TryParseExact(
            value,
            CreateClientFromLeadCommand.DateOfBirthFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;
}

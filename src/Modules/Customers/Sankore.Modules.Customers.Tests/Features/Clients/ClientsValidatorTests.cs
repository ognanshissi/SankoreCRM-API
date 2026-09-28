namespace Sankore.Modules.Customers.Tests.Features.Clients;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.CreateIndividualClient;
using Sankore.Modules.Customers.Features.Clients.RevealSensitiveField;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Features.Clients.UpdateClient;
using Sankore.Modules.Customers.Features.Clients.UpdateClientSensitive;
using Xunit;

/// <summary>
/// Shape-level rules only. Everything that needs the database or a tenant setting
/// (minimum age, duplicates, agency perimeter, concurrency) is a handler concern and is
/// covered by the handler tests.
/// </summary>
public sealed class ClientsValidatorTests
{
    private static readonly Guid AgencyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ClientId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    // ── CreateIndividualClient ──────────────────────────────────────────────

    [Fact]
    public void Accepts_a_well_formed_creation_request()
    {
        var result = new CreateIndividualClientValidator().Validate(CreateCommand());

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_a_creation_without_a_last_name(string lastName)
    {
        var result = new CreateIndividualClientValidator()
            .Validate(CreateCommand() with { LastName = lastName });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateIndividualClientCommand.LastName));
    }

    [Fact]
    public void Rejects_a_date_of_birth_in_the_future()
    {
        var tomorrow = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime).AddDays(1);

        var result = new CreateIndividualClientValidator()
            .Validate(CreateCommand() with { DateOfBirth = tomorrow });

        // A future birth date is a typo. The MINIMUM age is a tenant setting, so it stays
        // a handler rule (CLIENT_UNDER_MINIMUM_AGE), not a validator one.
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_creation_with_no_reachable_phone_number()
    {
        var result = new CreateIndividualClientValidator()
            .Validate(CreateCommand() with { PhoneNumbers = [] });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_creation_whose_only_phone_number_is_blank()
    {
        var result = new CreateIndividualClientValidator()
            .Validate(CreateCommand() with { PhoneNumbers = ["   "] });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_malformed_email()
    {
        var result = new CreateIndividualClientValidator()
            .Validate(CreateCommand() with { Email = "pas-une-adresse" });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_declared_income_without_its_currency()
    {
        var result = new CreateIndividualClientValidator()
            .Validate(CreateCommand() with { DeclaredIncome = 500000m, DeclaredIncomeCurrency = null });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_negative_declared_income()
    {
        var result = new CreateIndividualClientValidator()
            .Validate(CreateCommand() with { DeclaredIncome = -1m, DeclaredIncomeCurrency = "XOF" });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_currency_that_is_not_a_three_letter_code()
    {
        var result = new CreateIndividualClientValidator()
            .Validate(CreateCommand() with { DeclaredIncome = 1m, DeclaredIncomeCurrency = "FRANC" });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rejects_an_empty_identity_document_number()
    {
        var result = new CreateIndividualClientValidator()
            .Validate(CreateCommand() with { IdentityDocumentNumber = "" });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_document_expiring_before_it_was_issued()
    {
        var result = new CreateIndividualClientValidator()
            .Validate(CreateCommand() with
            {
                IdentityDocumentIssuedOn = new DateOnly(2030, 1, 1),
                IdentityDocumentExpiresOn = new DateOnly(2025, 1, 1),
            });

        result.IsValid.Should().BeFalse();
    }

    // ── UpdateClient ────────────────────────────────────────────────────────

    [Fact]
    public void Accepts_an_update_that_changes_nothing()
    {
        // Every field is "null means leave unchanged", so an empty PATCH is valid.
        var result = new UpdateClientValidator().Validate(UpdateCommand());

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Rejects_an_update_without_a_client_id()
    {
        var result = new UpdateClientValidator()
            .Validate(UpdateCommand() with { ClientId = Guid.Empty });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rejects_an_update_with_a_negative_declared_income()
    {
        var result = new UpdateClientValidator()
            .Validate(UpdateCommand() with { DeclaredIncome = -5m });

        result.IsValid.Should().BeFalse();
    }

    // ── UpdateClientSensitive ───────────────────────────────────────────────

    [Fact]
    public void Accepts_a_sensitive_update_with_a_proper_motive()
    {
        var result = new UpdateClientSensitiveValidator().Validate(SensitiveCommand());

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("maj")]
    [InlineData("erreur")]
    public void Rejects_a_sensitive_update_whose_motive_is_missing_or_too_short(string reason)
    {
        var result = new UpdateClientSensitiveValidator()
            .Validate(SensitiveCommand() with { Reason = reason });

        result.IsValid.Should().BeFalse();
        // The failure message IS the contract's error code, so the front-end localises the
        // same string whether the rejection came from here (400) or from the handler.
        result.Errors.Should().Contain(e => e.ErrorMessage == CustomerErrors.ReasonRequired);
    }

    [Fact]
    public void Rejects_a_sensitive_update_that_blanks_the_document_number()
    {
        // Null means "leave unchanged"; an empty string would mean "erase", which this
        // endpoint must never do to a regulated field.
        var result = new UpdateClientSensitiveValidator()
            .Validate(SensitiveCommand() with { IdentityDocumentNumber = "" });

        result.IsValid.Should().BeFalse();
    }

    // ── RevealSensitiveField ────────────────────────────────────────────────

    [Fact]
    public void Accepts_a_reveal_of_a_known_field()
    {
        var result = new RevealSensitiveFieldValidator()
            .Validate(new RevealSensitiveFieldCommand(ClientId, SensitiveField.Phone, null));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_reveal_of_a_field_outside_the_enumeration()
    {
        var result = new RevealSensitiveFieldValidator()
            .Validate(new RevealSensitiveFieldCommand(ClientId, (SensitiveField)999, null));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == CustomerErrors.UnknownSensitiveField);
    }

    [Fact]
    public void Rejects_a_reveal_without_a_client_id()
    {
        var result = new RevealSensitiveFieldValidator()
            .Validate(new RevealSensitiveFieldCommand(Guid.Empty, SensitiveField.Phone, null));

        result.IsValid.Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static CreateIndividualClientCommand CreateCommand()
        => new(
            AgencyId: AgencyId,
            AdvisorUserId: null,
            FirstName: "Awa",
            LastName: "Traoré",
            MaidenName: null,
            Gender: Gender.Female,
            DateOfBirth: new DateOnly(1990, 4, 12),
            BirthPlace: "Abidjan",
            Nationality: "CI",
            MaritalStatus: MaritalStatus.Single,
            FatherName: null,
            MotherName: null,
            Profession: "Commerçante",
            Employer: null,
            DeclaredIncome: null,
            DeclaredIncomeCurrency: null,
            PreferredLanguage: "fr",
            IdentityDocumentType: IdentityDocumentType.NationalIdCard,
            IdentityDocumentNumber: "CI0012345642",
            IdentityDocumentIssuedOn: new DateOnly(2022, 1, 10),
            IdentityDocumentExpiresOn: new DateOnly(2032, 1, 9),
            PhoneNumbers: ["+225 07 08 09 18"],
            Email: null,
            Address: new PostalAddressInput("12 rue des Jardins", "Abidjan", "Lagunes", "CI", "01"),
            ConfirmNoDuplicate: false);

    private static UpdateClientCommand UpdateCommand()
        => new(
            ClientId: ClientId,
            ExpectedVersion: 0,
            Profession: null,
            Employer: null,
            MaritalStatus: null,
            DeclaredIncome: null,
            DeclaredIncomeCurrency: null,
            PreferredLanguage: null);

    private static UpdateClientSensitiveCommand SensitiveCommand()
        => new(
            ClientId: ClientId,
            ExpectedVersion: 0,
            Reason: "Carte nationale d'identité renouvelée en agence",
            FirstName: null,
            LastName: null,
            MaidenName: null,
            IdentityDocumentType: null,
            IdentityDocumentNumber: null,
            IdentityDocumentIssuedOn: null,
            IdentityDocumentExpiresOn: null,
            Address: null);
}

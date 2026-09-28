namespace Sankore.Modules.Customers.Tests.Features.LegalEntities;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Features.LegalEntities.CreateLegalClient;
using Sankore.Modules.Customers.Features.LegalEntities.CreateLegalForm;
using Sankore.Modules.Customers.Features.LegalEntities.DeclareBeneficialOwners;
using Xunit;

/// <summary>
/// The "valid data required" acceptance criterion of US-M01-BE-17 is a validator
/// concern, so it is asserted here rather than through the handlers.
/// </summary>
public sealed class LegalEntitiesValidatorTests
{
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private static CreateLegalClientCommand ValidCreate() => new(
        AgencyId: AgencyId,
        AdvisorUserId: null,
        LegalName: "Sankore Distribution",
        LegalFormCode: "SARL",
        RegistrationNumber: "CI-ABJ-2019-B-12345",
        TaxIdNumber: "CI1234567890",
        IncorporationDate: new DateOnly(2019, 4, 12),
        HeadOfficeAddress: new PostalAddressInput("Rue des Jardins", "Abidjan", "Cocody", "CI", "01BP1234"),
        PhoneNumbers: ["+2250708091810"],
        Email: "contact@sankore.ci",
        PreferredLanguage: "fr");

    [Fact]
    public void Accepts_a_complete_legal_client_payload()
        => new CreateLegalClientValidator().Validate(ValidCreate()).IsValid.Should().BeTrue();

    [Theory]
    [InlineData(nameof(CreateLegalClientCommand.LegalName))]
    [InlineData(nameof(CreateLegalClientCommand.LegalFormCode))]
    [InlineData(nameof(CreateLegalClientCommand.RegistrationNumber))]
    public void Rejects_a_missing_mandatory_identity_field(string field)
    {
        var command = field switch
        {
            nameof(CreateLegalClientCommand.LegalName) => ValidCreate() with { LegalName = "  " },
            nameof(CreateLegalClientCommand.LegalFormCode) => ValidCreate() with { LegalFormCode = "" },
            _ => ValidCreate() with { RegistrationNumber = "" },
        };

        var result = new CreateLegalClientValidator().Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == field);
    }

    [Fact]
    public void Rejects_a_missing_incorporation_date()
    {
        var result = new CreateLegalClientValidator()
            .Validate(ValidCreate() with { IncorporationDate = default });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(CreateLegalClientCommand.IncorporationDate));
    }

    [Fact]
    public void Rejects_an_incorporation_date_in_the_future()
    {
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));

        new CreateLegalClientValidator()
            .Validate(ValidCreate() with { IncorporationDate = tomorrow })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_head_office_address_with_neither_street_nor_city()
    {
        var result = new CreateLegalClientValidator().Validate(
            ValidCreate() with { HeadOfficeAddress = new PostalAddressInput(null, null, null, "CI", null) });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(CreateLegalClientCommand.HeadOfficeAddress));
    }

    [Fact]
    public void Rejects_a_legal_client_without_any_contact()
    {
        var result = new CreateLegalClientValidator().Validate(
            ValidCreate() with { PhoneNumbers = [], Email = null });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.ErrorMessage.Contains("At least one contact", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Accepts_a_single_contact_of_either_kind(bool withPhone, bool withEmail)
    {
        var command = ValidCreate() with
        {
            PhoneNumbers = withPhone ? ["+2250708091810"] : [],
            Email = withEmail ? "contact@sankore.ci" : null,
        };

        new CreateLegalClientValidator().Validate(command).IsValid.Should().BeTrue();
    }

    // ── Beneficial owners ───────────────────────────────────────────────────

    private static DeclareBeneficialOwnersCommand ValidDeclaration(
        params BeneficialOwnerInput[] owners) =>
        new(
            ClientId: Guid.NewGuid(),
            Owners: owners.Length == 0
                ? [new BeneficialOwnerInput(null, "Kouassi Adjoua", "CI", null, null, 100m, ControlType.Ownership)]
                : owners,
            Reason: "Annual AML review of the ownership structure");

    [Fact]
    public void Accepts_a_well_formed_declaration()
        => new DeclareBeneficialOwnersValidator().Validate(ValidDeclaration()).IsValid.Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too short")]
    public void Rejects_a_declaration_without_a_usable_reason(string reason)
    {
        var result = new DeclareBeneficialOwnersValidator()
            .Validate(ValidDeclaration() with { Reason = reason });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(DeclareBeneficialOwnersCommand.Reason));
    }

    [Fact]
    public void Rejects_an_owner_that_is_neither_a_client_nor_an_external_person()
    {
        var result = new DeclareBeneficialOwnersValidator().Validate(ValidDeclaration(
            new BeneficialOwnerInput(null, null, null, null, null, 100m, ControlType.Ownership)));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rejects_an_owner_that_is_both_a_client_and_an_external_person()
    {
        var result = new DeclareBeneficialOwnersValidator().Validate(ValidDeclaration(
            new BeneficialOwnerInput(Guid.NewGuid(), "Kouassi Adjoua", null, null, null, 100m, ControlType.Ownership)));

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Rejects_an_ownership_percentage_outside_the_zero_to_hundred_range(int percentage)
    {
        var result = new DeclareBeneficialOwnersValidator().Validate(ValidDeclaration(
            new BeneficialOwnerInput(null, "Kouassi Adjoua", null, null, null, percentage, ControlType.Ownership)));

        result.IsValid.Should().BeFalse();
    }

    // ── Legal forms ─────────────────────────────────────────────────────────

    [Fact]
    public void Accepts_a_well_formed_legal_form()
        => new CreateLegalFormValidator()
            .Validate(new CreateLegalFormCommand("SCOOPS", "Société coopérative simplifiée", 7))
            .IsValid.Should().BeTrue();

    [Theory]
    [InlineData("", "Label")]
    [InlineData("SARL", "")]
    [InlineData("SA RL", "Label")]
    [InlineData("SA/RL", "Label")]
    public void Rejects_a_malformed_legal_form(string code, string label)
        => new CreateLegalFormValidator()
            .Validate(new CreateLegalFormCommand(code, label, 0))
            .IsValid.Should().BeFalse();
}

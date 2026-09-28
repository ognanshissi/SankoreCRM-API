namespace Sankore.Modules.Customers.Tests.Features.LeadConversion;

using FluentAssertions;
using Sankore.Modules.Customers.Features.LeadConversion.CreateClientFromLead;
using Xunit;

/// <summary>
/// The validator is the module boundary's gate: this command does not come from a bound HTTP
/// request, it comes from another module's code, so the shape checks are the only thing between
/// a malformed call and the aggregate.
/// </summary>
public sealed class CreateClientFromLeadValidatorTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid LeadId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    private readonly CreateClientFromLeadValidator _validator = new();

    private static CreateClientFromLeadCommand Command(
        Guid? tenantId = null,
        string? firstName = "Awa",
        string? lastName = "Kone",
        string? legalName = null,
        string? email = "awa.kone@example.ci",
        string? dateOfBirth = "1990-03-17") =>
        new(
            TenantId: tenantId ?? TenantId,
            LeadId: LeadId,
            AgencyId: AgencyId,
            ConvertedByUserId: UserId,
            FirstName: firstName,
            LastName: lastName,
            LegalName: legalName,
            Gender: "Female",
            DateOfBirth: dateOfBirth,
            Nationality: "CI",
            PhoneNumber: "+2250708091810",
            Email: email,
            IdentityDocumentType: "NationalIdCard",
            IdentityDocumentNumber: "CI-0123456789",
            Profession: "Commerçante",
            PreferredLanguage: "fr",
            RequestedClientId: null);

    [Fact]
    public void Accepts_an_individual_lead_with_both_names()
        => _validator.Validate(Command()).IsValid.Should().BeTrue();

    [Fact]
    public void Accepts_a_legal_lead_with_only_a_legal_name()
        => _validator
            .Validate(Command(firstName: null, lastName: null, legalName: "Sankore Distribution"))
            .IsValid.Should().BeTrue();

    [Fact]
    public void Rejects_a_lead_with_neither_a_person_name_nor_a_legal_name()
    {
        var result = _validator.Validate(Command(firstName: null, lastName: null));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == "LEAD_IDENTITY_INCOMPLETE");
    }

    [Fact]
    public void Rejects_a_lead_carrying_only_half_of_a_person_name()
    {
        // CreateIndividual rejects a blank half anyway; refusing here turns a DomainException
        // into a validation answer the caller can act on.
        var result = _validator.Validate(Command(lastName: null));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == "LEAD_IDENTITY_INCOMPLETE");
    }

    [Fact]
    public void Rejects_a_missing_tenant()
        => _validator.Validate(Command(tenantId: Guid.Empty)).IsValid.Should().BeFalse();

    [Fact]
    public void Rejects_a_date_of_birth_in_the_future()
        => _validator
            .Validate(Command(
                dateOfBirth: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1).ToString("yyyy-MM-dd")))
            .IsValid.Should().BeFalse();

    [Fact]
    public void Rejects_a_date_of_birth_that_is_not_iso_formatted()
        => _validator.Validate(Command(dateOfBirth: "17/03/1990")).IsValid.Should().BeFalse();

    [Fact]
    public void Accepts_a_lead_without_a_date_of_birth()
        => _validator.Validate(Command(dateOfBirth: null)).IsValid.Should().BeTrue();

    [Fact]
    public void Rejects_a_malformed_email()
        => _validator.Validate(Command(email: "not-an-email")).IsValid.Should().BeFalse();

    [Fact]
    public void Accepts_a_lead_without_an_email()
        => _validator.Validate(Command(email: null)).IsValid.Should().BeTrue();
}

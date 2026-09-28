namespace Sankore.Modules.Customers.Tests.Features.Relationships;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Relationships.AddRelationship;
using Xunit;

public sealed class AddRelationshipValidatorTests
{
    private readonly AddRelationshipValidator _validator = new();

    private static AddRelationshipCommand Command(
        Guid? relatedClientId = null,
        string? externalFullName = null,
        string? phone = null,
        DateOnly? dateOfBirth = null,
        string? document = null)
        => new(Guid.NewGuid(), RelationshipType.Guarantor, relatedClientId, externalFullName,
            phone, dateOfBirth, document);

    [Fact]
    public void A_related_client_alone_is_valid()
        => _validator.Validate(Command(relatedClientId: Guid.NewGuid())).IsValid.Should().BeTrue();

    [Fact]
    public void An_external_name_alone_is_valid()
        => _validator.Validate(Command(externalFullName: "Fatou Diarra")).IsValid.Should().BeTrue();

    [Fact]
    public void Naming_both_a_related_client_and_an_external_person_is_rejected()
        => _validator
            .Validate(Command(relatedClientId: Guid.NewGuid(), externalFullName: "Fatou Diarra"))
            .IsValid.Should().BeFalse();

    [Fact]
    public void Naming_neither_party_is_rejected()
        => _validator.Validate(Command()).IsValid.Should().BeFalse();

    [Fact]
    public void Identity_details_are_rejected_when_the_target_is_a_client_of_the_tenant()
        => _validator
            .Validate(Command(relatedClientId: Guid.NewGuid(), phone: "0708091801"))
            .IsValid.Should().BeFalse();

    [Fact]
    public void A_phone_number_shorter_than_eight_digits_is_rejected()
        => _validator
            .Validate(Command(externalFullName: "Fatou Diarra", phone: "0708"))
            .IsValid.Should().BeFalse();

    [Fact]
    public void A_date_of_birth_in_the_future_is_rejected()
        => _validator
            .Validate(Command(
                externalFullName: "Fatou Diarra",
                dateOfBirth: DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1))))
            .IsValid.Should().BeFalse();
}

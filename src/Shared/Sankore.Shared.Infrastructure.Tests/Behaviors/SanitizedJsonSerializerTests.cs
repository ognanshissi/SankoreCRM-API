namespace Sankore.Shared.Infrastructure.Tests.Behaviors;

using System.Text.Json;
using FluentAssertions;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The audit pipeline serializes EVERY command through this class, so a sensitive
/// property it cannot handle does not merely leak — it throws, and the whole write
/// fails. These tests pin the contract for the property types real commands use.
/// </summary>
public sealed class SanitizedJsonSerializerTests
{
    private sealed record StringCommand(
        string Email,
        [property: SensitiveData] string Password);

    private sealed record NonStringCommand(
        Guid ClientId,
        [property: SensitiveData] DateOnly? DateOfBirth,
        [property: SensitiveData] IReadOnlyList<string> PhoneNumbers,
        [property: SensitiveData] decimal? DeclaredIncome,
        [property: SensitiveData] Address? PostalAddress);

    private sealed record Address(string Street, string City);

    private sealed record NullSensitiveCommand(
        [property: SensitiveData] string? IdentityDocumentNumber,
        [property: SensitiveData] DateOnly? DateOfBirth);

    private static Dictionary<string, JsonElement> Parse(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Redacts_a_sensitive_string_and_keeps_the_rest()
    {
        var json = SanitizedJsonSerializer.Serialize(
            new StringCommand("awa@example.ci", "s3cr3t-passphrase"));

        var payload = Parse(json);
        payload["Email"].GetString().Should().Be("awa@example.ci");
        payload["Password"].GetString().Should().Be("***");
        json.Should().NotContain("s3cr3t-passphrase");
    }

    [Fact]
    public void Redacts_sensitive_properties_that_are_not_strings()
    {
        // Regression: replacing the getter with "***" made System.Text.Json cast a
        // string back to DateOnly?/IReadOnlyList<string>/a nested record and throw
        // InvalidCastException, so every client-creation command failed at audit time.
        var command = new NonStringCommand(
            Guid.NewGuid(),
            new DateOnly(1987, 4, 2),
            ["+2250708091801", "+33799887766"],
            450_000m,
            new Address("12 Rue Carnot", "Abidjan"));

        var act = () => SanitizedJsonSerializer.Serialize(command);

        act.Should().NotThrow();

        var json = act();
        var payload = Parse(json);
        payload["DateOfBirth"].GetString().Should().Be("***");
        payload["PhoneNumbers"].GetString().Should().Be("***");
        payload["DeclaredIncome"].GetString().Should().Be("***");
        payload["PostalAddress"].GetString().Should().Be("***");

        json.Should().NotContain("1987");
        json.Should().NotContain("0708091801");
        json.Should().NotContain("799887766");
        json.Should().NotContain("450000");
        json.Should().NotContain("Carnot");
        json.Should().NotContain("Abidjan");
    }

    [Fact]
    public void Keeps_the_property_name_so_the_audit_records_which_fields_were_carried()
    {
        var json = SanitizedJsonSerializer.Serialize(
            new NonStringCommand(Guid.NewGuid(), null, [], null, null));

        // The compliance requirement is "encrypted fields are logged by name only":
        // the name must survive, the value must not.
        Parse(json).Should().ContainKeys(
            "ClientId", "DateOfBirth", "PhoneNumbers", "DeclaredIncome", "PostalAddress");
    }

    [Fact]
    public void Redacts_a_null_sensitive_value_rather_than_revealing_its_absence()
    {
        var json = SanitizedJsonSerializer.Serialize(new NullSensitiveCommand(null, null));

        var payload = Parse(json);
        payload["IdentityDocumentNumber"].GetString().Should().Be("***");
        payload["DateOfBirth"].GetString().Should().Be("***");
    }
}

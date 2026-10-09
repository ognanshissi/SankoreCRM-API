namespace Sankore.Modules.Integration.Tests.Features.Connections;

using System.Reflection;
using FluentAssertions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Connections;
using Xunit;

/// <summary>
/// No endpoint of this area may ever return a credential (INT-03, criterion 3).
///
/// <para>
/// The guard is on the NAMES rather than on any value, because that is how this leak actually
/// happens: nobody writes <c>Password = connection.Password</c>; somebody adds one convenient
/// field to a response record months later. A property called <c>apiKey</c> on a DTO is a leak
/// whatever it holds today, so the test fails on the name and the discussion happens at review
/// time instead of after a disclosure.
/// </para>
/// </summary>
public sealed class ConnectionDtoSecrecyTests
{
    private static readonly string[] ForbiddenWords = ["secret", "password", "token", "apikey"];

    /// <summary>
    /// Every record an endpoint of this area returns. Listed by hand rather than discovered, so
    /// that a new response type has to be added here — and the author has to read why.
    /// </summary>
    public static TheoryData<Type> ResponseRecords() => new(
        typeof(ConnectionListDto),
        typeof(ConnectionDetailDto),
        typeof(ConnectionHealthDto),
        typeof(ConnectionSecretStatusDto));

    [Theory]
    [MemberData(nameof(ResponseRecords))]
    public void No_response_record_declares_a_property_whose_name_evokes_a_secret(Type dto)
    {
        var offenders = PublicProperties(dto)
            .Select(p => p.Name)
            .Where(IsSuspicious)
            .ToList();

        offenders.Should().BeEmpty(
            "{0} is returned by an endpoint; a credential must never be reachable through it",
            dto.Name);
    }

    [Fact]
    public void The_status_record_exposes_a_masked_hint_and_no_value()
    {
        var names = PublicProperties(typeof(ConnectionSecretStatusDto)).Select(p => p.Name).ToList();

        // Enough to tell two credentials apart while rotating one, never enough to use either.
        names.Should().Contain("MaskedValue");
        names.Should().NotContain("Value");
    }

    /// <summary>
    /// The settings records travel inside <see cref="ConnectionDetailDto"/>, so they are part of
    /// the response surface and are checked too.
    ///
    /// <para>
    /// One name is allowed through, and it is the reason this test is not a blanket substring
    /// match: <c>TemenosSettings.TokenEndpoint</c> is the URL of Temenos' OAuth token endpoint —
    /// a coordinate, like the base URL next to it. The SECRET exchanged there lives in the vault
    /// and the settings object holds only <c>CredentialVaultRef</c>, a reference. Anything else
    /// matching the forbidden words is a real finding.
    /// </para>
    /// </summary>
    [Fact]
    public void No_settings_record_declares_a_credential_shaped_property()
    {
        string[] allowed = ["TokenEndpoint"];

        var settingsTypes = typeof(ConnectionSettings).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(ConnectionSettings).IsAssignableFrom(t))
            .ToList();

        settingsTypes.Should().NotBeEmpty("the assembly scan must actually find the settings records");

        var offenders = settingsTypes
            .SelectMany(t => PublicProperties(t).Select(p => $"{t.Name}.{p.Name}"))
            .Where(name => IsSuspicious(name.Split('.')[1]))
            .Where(name => !allowed.Contains(name.Split('.')[1], StringComparer.Ordinal))
            .ToList();

        offenders.Should().BeEmpty(
            "connection settings are returned by GET connections/{{id}}: they hold coordinates "
            + "and vault references, never values");
    }

    private static IEnumerable<PropertyInfo> PublicProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

    private static bool IsSuspicious(string propertyName)
        => ForbiddenWords.Any(word =>
            propertyName.Contains(word, StringComparison.OrdinalIgnoreCase));
}

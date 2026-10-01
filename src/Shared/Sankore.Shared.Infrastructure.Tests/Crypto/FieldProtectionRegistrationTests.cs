namespace Sankore.Shared.Infrastructure.Tests.Crypto;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sankore.Shared.Infrastructure.Crypto;
using Xunit;

/// <summary>
/// `AddFieldProtection` takes a section name, which reads as "each module brings its own keys".
/// It does not: `FieldProtectionOptions` is one options instance for the container and
/// `IFieldEncryptor` one singleton, so a second call binds over the first and every module ends up
/// sharing whichever key was registered last.
///
/// Measured before this guard existed: registering "Customers" then "Kyc" resolved to the Kyc key
/// for both. M01 has client PII already encrypted in production with the Customers key — the first
/// module to register a second section would have made all of it undecryptable, silently, with the
/// damage only visible the next time someone opened a client record.
/// </summary>
public sealed class FieldProtectionRegistrationTests
{
    private static IConfiguration TwoSections() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Customers:FieldEncryptionKey"] = Convert.ToBase64String(new byte[32]),
            ["Customers:BlindIndexKey"] = Convert.ToBase64String(new byte[32]),
            ["Kyc:FieldEncryptionKey"] = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()),
            ["Kyc:BlindIndexKey"] = Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()),
        }).Build();

    [Fact]
    public void A_second_section_is_refused_instead_of_silently_sharing_a_key()
    {
        var services = new ServiceCollection();
        services.AddFieldProtection(TwoSections(), "Customers");

        var act = () => services.AddFieldProtection(TwoSections(), "Kyc");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
                .Contain("Customers").And.Contain("Kyc")
                .And.Contain("own options type", "the message must say what to do instead");
    }

    [Fact]
    public void Registering_the_same_section_twice_is_harmless()
    {
        // Two modules may both depend on M01's protection; that is not the dangerous case.
        var services = new ServiceCollection();
        var config = TwoSections();

        services.AddFieldProtection(config, "Customers");
        var act = () => services.AddFieldProtection(config, "Customers");

        act.Should().NotThrow();
    }

    [Fact]
    public void The_bound_section_is_the_one_configured()
    {
        var services = new ServiceCollection();
        services.AddFieldProtection(TwoSections(), "Customers");

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<FieldProtectionOptions>>().Value;

        options.SectionName.Should().Be("Customers");
        options.FieldEncryptionKey.Should().Be(Convert.ToBase64String(new byte[32]));
    }

    [Fact]
    public void A_misconfigured_key_names_the_section_the_operator_must_fix()
    {
        // It used to always say "Customers:FieldEncryptionKey", whatever section was bound.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kyc:FieldEncryptionKey"] = "not-base64",
            ["Kyc:BlindIndexKey"] = Convert.ToBase64String(new byte[32]),
        }).Build();

        var services = new ServiceCollection();
        services.AddFieldProtection(config, "Kyc");

        using var sp = services.BuildServiceProvider();
        var encryptor = sp.GetRequiredService<IFieldEncryptor>();

        var act = () => encryptor.Encrypt("x");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().StartWith("Kyc:FieldEncryptionKey");
    }
}

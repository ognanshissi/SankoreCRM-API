namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sankore.Shared.Infrastructure.Secrets;
using Xunit;

/// <summary>
/// The vault's master key used to be validated nowhere: a missing or malformed
/// Secrets:EncryptionKey surfaced as ArgumentNullException (Parameter 's') from
/// Convert.FromBase64String on the FIRST write — a 500 for whoever happened to save an email
/// provider, hours after the deployment that caused it. These tests pin the boot-time refusal.
/// </summary>
public sealed class SecretsVaultRegistrationTests
{
    private static SecretsOptions Resolve(string? encryptionKey)
    {
        var values = new Dictionary<string, string?>();
        if (encryptionKey is not null)
            values["Secrets:EncryptionKey"] = encryptionKey;

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddSecretsVault(
            config,
            opts => opts.UseInMemoryDatabase($"secrets-{Guid.NewGuid()}"));

        using var sp = services.BuildServiceProvider();

        // Resolving the options runs the same validators ValidateOnStart runs at boot.
        return sp.GetRequiredService<IOptions<SecretsOptions>>().Value;
    }

    [Fact]
    public void A_valid_32_byte_key_is_accepted()
    {
        var key = Convert.ToBase64String(new byte[32]);

        Resolve(key).EncryptionKey.Should().Be(key);
    }

    [Fact]
    public void A_missing_key_refuses_to_resolve_and_says_how_to_generate_one()
    {
        var act = () => Resolve(null);

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("Secrets:EncryptionKey is not configured")
            .And.Contain("openssl rand -base64 32");
    }

    [Fact]
    public void An_empty_key_is_treated_as_missing()
    {
        var act = () => Resolve("   ");

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("is not configured");
    }

    [Fact]
    public void A_key_that_is_not_base64_is_named_as_such()
    {
        // The failure mode that produced an unreadable FormatException deep inside a send.
        var act = () => Resolve("not-base64!!");

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("not valid Base64");
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(64)]
    public void A_key_of_the_wrong_length_is_refused_with_its_expected_size(int bytes)
    {
        // AesGcm would otherwise throw CryptographicException on the first encrypt.
        var act = () => Resolve(Convert.ToBase64String(new byte[bytes]));

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("32 bytes");
    }
}

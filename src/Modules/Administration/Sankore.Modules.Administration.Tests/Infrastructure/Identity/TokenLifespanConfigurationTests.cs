namespace Sankore.Modules.Administration.Tests.Infrastructure.Identity;

using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration;
using Sankore.Modules.Administration.Infrastructure.Identity;
using Xunit;

/// <summary>
/// Covers the wiring between the <c>Identity</c> configuration section and the two token
/// providers. The interesting property is that the values land on DIFFERENT options objects:
/// activation on its own, password reset on the framework-wide one every default provider
/// shares. Collapsing them back into one would silently stretch reset links to the activation
/// lifespan, and nothing else in the suite would notice.
/// </summary>
public sealed class TokenLifespanConfigurationTests
{
    private static (DataProtectionTokenProviderOptions Reset, ActivationTokenProviderOptions Activation)
        Resolve(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s =>
                new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddOptions();
        AdministrationModule.AddTokenLifespans(services, config);

        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>().Value,
                sp.GetRequiredService<IOptions<ActivationTokenProviderOptions>>().Value);
    }

    [Fact]
    public void Should_apply_each_configured_lifespan_to_its_own_provider()
    {
        var (reset, activation) = Resolve(
            ("Identity:ActivationTokenLifespan", "14.00:00:00"),
            ("Identity:PasswordResetTokenLifespan", "00:30:00"));

        activation.TokenLifespan.Should().Be(TimeSpan.FromDays(14));
        reset.TokenLifespan.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Should_not_let_the_activation_lifespan_leak_into_password_reset()
    {
        // Only activation is configured — reset must keep its own (short) default.
        var (reset, activation) = Resolve(("Identity:ActivationTokenLifespan", "30.00:00:00"));

        activation.TokenLifespan.Should().Be(TimeSpan.FromDays(30));
        reset.TokenLifespan.Should().Be(IdentityTokenOptions.DefaultPasswordResetLifespan);
    }

    [Fact]
    public void Should_fall_back_to_the_defaults_when_nothing_is_configured()
    {
        var (reset, activation) = Resolve();

        activation.TokenLifespan.Should().Be(IdentityTokenOptions.DefaultActivationLifespan);
        reset.TokenLifespan.Should().Be(IdentityTokenOptions.DefaultPasswordResetLifespan);
    }

    [Fact]
    public void Should_keep_the_provider_name_the_handlers_look_up()
    {
        // The name is part of the token's purpose chain: drift here invalidates every link.
        var (_, activation) = Resolve();
        activation.Name.Should().Be(ActivationTokens.ProviderName);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-01:00:00")]
    public void Should_refuse_to_start_on_a_non_positive_lifespan(string lifespan)
    {
        // ValidateOnStart runs through IStartupValidator; resolving the options is what trips it.
        var act = () => Resolve(("Identity:ActivationTokenLifespan", lifespan));

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*ActivationTokenLifespan*");
    }
}

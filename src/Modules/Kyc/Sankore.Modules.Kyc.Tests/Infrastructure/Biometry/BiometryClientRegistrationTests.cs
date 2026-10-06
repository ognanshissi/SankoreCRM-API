namespace Sankore.Modules.Kyc.Tests.Infrastructure.Biometry;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Modules.Kyc.Infrastructure.Biometry;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// <c>Kyc:Biometry:UseFake</c> was documented as "read by the module's registration" and was read
/// by nothing: <c>IBiometryClient</c> was wired to the HTTP client unconditionally, so the flag was
/// dead configuration. Combined with there being no way to store a service token, that left a dev
/// environment unable to reach biometry at all — every call short-circuited to
/// BIOMETRY_NOT_CONFIGURED while <c>FakeBiometryClient</c>, built to run the flow without Flask,
/// was reachable only from tests.
///
/// A comment cannot keep that honest, so these tests resolve the real container.
/// </summary>
public sealed class BiometryClientRegistrationTests
{
    private static ServiceProvider Build(string? useFake)
    {
        var settings = new Dictionary<string, string?>
        {
            // Enough for the module to register; these tests only resolve the biometry client.
            ["ConnectionStrings:Database"] = "Host=localhost;Database=none",
            ["Kyc:FieldEncryptionKey"] = Convert.ToBase64String(new byte[32]),
            ["Kyc:BlindIndexKey"] = Convert.ToBase64String(new byte[32]),
            ["Kyc:Storage:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            ["Kyc:Biometry:BaseUrl"] = "http://localhost:8000",
        };

        if (useFake is not null)
            settings["Kyc:Biometry:UseFake"] = useFake;

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new ServiceCollection()
            .AddLogging()
            // Supplied by the host through AddSecretsVault, not by the module: HttpBiometryClient
            // takes it as a dependency, and these tests are about WHICH client is selected, not
            // about the bootstrapper's wiring.
            .AddSingleton(Substitute.For<ISecretsModule>())
            .AddSingleton(Substitute.For<ITenantContext>())
            .AddKycModule(config)
            .BuildServiceProvider();
    }

    [Fact]
    public void UseFake_true_resolves_the_fake_client()
    {
        using var sp = Build("true");
        using var scope = sp.CreateScope();

        scope.ServiceProvider.GetRequiredService<IBiometryClient>()
            .Should().BeOfType<FakeBiometryClient>(
                "the flag is what lets the KYC flow run with no Flask deployment");
    }

    [Fact]
    public void UseFake_false_resolves_the_http_client()
    {
        using var sp = Build("false");
        using var scope = sp.CreateScope();

        scope.ServiceProvider.GetRequiredService<IBiometryClient>()
            .Should().BeOfType<HttpBiometryClient>();
    }

    [Fact]
    public void The_http_client_is_the_default_when_the_flag_is_absent()
    {
        // Absent must not mean fake: a deployment that forgets the key has to talk to the real
        // service, not silently approve everything with plausible answers.
        using var sp = Build(useFake: null);
        using var scope = sp.CreateScope();

        scope.ServiceProvider.GetRequiredService<IBiometryClient>()
            .Should().BeOfType<HttpBiometryClient>();
    }
}

namespace Sankore.Modules.Kyc.Tests.Infrastructure.Storage;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Sankore.Modules.Kyc;
using Sankore.Shared.ObjectStorage;
using Xunit;

/// <summary>
/// Which medium M02's evidence actually lands on.
///
/// <para>
/// The module registers a filesystem backend with <c>TryAdd</c> and the host declares the real
/// one before it. That ordering is the entire mechanism, and getting it wrong is silent: the
/// module's fallback would win, KYC evidence would be written to a container volume that the next
/// redeploy discards, and nothing would say so until a regulator asked for a document that is no
/// longer there. Nothing else in the suite would notice, so it is pinned here.
/// </para>
/// </summary>
public sealed class KycObjectBackendRegistrationTests
{
    private static IConfiguration Configuration(bool withBucket)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = "Host=localhost;Database=unused",
            ["Kyc:Storage:BasePath"] = Path.Combine(Path.GetTempPath(), "sankore-kyc-registration"),
            ["Kyc:Storage:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
        };

        if (withBucket)
        {
            values["ObjectStorage:R2:AccountId"] = "0123456789abcdef0123456789abcdef";
            values["ObjectStorage:R2:AccessKeyId"] = "access-key-id";
            values["ObjectStorage:R2:SecretAccessKey"] = "secret-access-key";
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <param name="hostDeclares">
    /// False stands for a host that never calls <c>AddObjectBackend</c> at all — which is the only
    /// situation where the module's OWN fallback is what runs.
    /// </param>
    private static IObjectBackend ResolveBackend(
        bool withBucket, bool hostDeclares = true, bool hostDeclaresFirst = true)
    {
        var config = Configuration(withBucket);
        var services = new ServiceCollection();
        services.AddLogging();

        // The module's fallback factory reads IOptions<KycStorageOptions>, whose PostConfigure
        // asks for this. A real host always has one; registering it here is what lets the
        // no-host-declaration case below be exercised rather than throw.
        services.AddSingleton(HostEnvironment());

        void DeclareHostBackend() => services.AddObjectBackend(
            config,
            KycModule.ObjectStorageConcern,
            KycModule.ResolveObjectStorageBasePath(config, HostEnvironment()));

        if (hostDeclares && hostDeclaresFirst) DeclareHostBackend();
        services.AddKycModule(config);
        if (hostDeclares && !hostDeclaresFirst) DeclareHostBackend();

        return services.BuildServiceProvider()
            .GetRequiredKeyedService<IObjectBackend>(KycModule.ObjectStorageConcern);
    }

    [Fact]
    public void With_a_bucket_configured_the_module_fallback_must_stand_down()
        => ResolveBackend(withBucket: true).Should().BeOfType<R2ObjectBackend>(
            "a deployment that configured a bucket must not keep writing evidence to a container volume");

    [Fact]
    public void With_no_bucket_configured_the_host_declares_the_filesystem()
        => ResolveBackend(withBucket: false).Should().BeOfType<LocalObjectBackend>(
            "a developer machine and a single-node install must work with no configuration at all");

    /// <summary>
    /// A host that never declares a backend at all — anything embedding this module without the
    /// bootstrapper's wiring. The module must still be usable on its own, which is the reason its
    /// registration exists rather than being deleted in favour of the host's.
    /// </summary>
    [Fact]
    public void A_host_that_declares_nothing_still_gets_the_modules_own_fallback()
        => ResolveBackend(withBucket: false, hostDeclares: false)
            .Should().BeOfType<LocalObjectBackend>();

    /// <summary>
    /// The ordering in <c>Program.cs</c> is deliberate, but it must not be the only thing standing
    /// between a bucket and a container volume — so the reverse order is pinned to resolve to R2
    /// as well. If this ever starts failing, the comment in Program.cs stops being a nicety and
    /// becomes the only guard, which is worth knowing.
    /// </summary>
    [Fact]
    public void The_host_backend_must_win_even_if_it_is_declared_after_the_module()
        => ResolveBackend(withBucket: true, hostDeclaresFirst: false).Should().BeOfType<R2ObjectBackend>();

    private static IHostEnvironment HostEnvironment()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(Path.GetTempPath());
        return env;
    }
}

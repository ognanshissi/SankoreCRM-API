namespace Sankore.Modules.Integration.Infrastructure.Crypto;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// This module's own field-encryption key, registered under a DI key (INT-05).
///
/// <para>
/// NOT <c>services.AddFieldProtection(config, "Integration")</c>. That helper binds ONE
/// <c>FieldProtectionOptions</c> and ONE singleton for the whole container; M01 already holds
/// that slot for "Customers", and a second call now throws at start-up precisely because the
/// earlier behaviour was worse — the last registration won and every module silently shared one
/// key. M02 solved it the same way with <c>KycFieldProtection</c>.
/// </para>
///
/// <para>
/// A command payload is a customer's identity document, address and declared income on its way
/// out of the platform, sitting at rest for as long as the external system is down. It must not
/// be decryptable with the key that opens M01's columns, and the two must rotate independently.
/// </para>
/// </summary>
public static class IntegrationFieldProtection
{
    /// <summary>DI key for this module's encryptor and indexer.</summary>
    public const string Key = "Integration";

    /// <summary>Configuration section: Integration:FieldEncryptionKey, Integration:BlindIndexKey.</summary>
    public const string SectionName = "Integration";

    public static IServiceCollection AddIntegrationFieldProtection(
        this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        // Built here rather than through services.Configure: a named options instance would still
        // be reachable as the unnamed one by anything resolving IOptions<FieldProtectionOptions>,
        // and the whole point is that nothing outside this module ever sees these keys.
        var options = Options.Create(new FieldProtectionOptions
        {
            SectionName = SectionName,
            FieldEncryptionKey = config[$"{SectionName}:FieldEncryptionKey"]!,
            BlindIndexKey = config[$"{SectionName}:BlindIndexKey"]!,
        });

        // Validated lazily, on first use, by the primitives: a misconfigured integration key must
        // not stop the whole API from booting for the modules that do not need it.
        services.AddKeyedSingleton<IFieldEncryptor>(Key, (_, _) => new AesGcmFieldEncryptor(options));
        services.AddKeyedSingleton<IBlindIndexer>(Key, (_, _) => new HmacBlindIndexer(options));

        return services;
    }
}

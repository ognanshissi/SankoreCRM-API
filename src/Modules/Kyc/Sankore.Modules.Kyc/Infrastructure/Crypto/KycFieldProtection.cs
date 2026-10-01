namespace Sankore.Modules.Kyc.Infrastructure.Crypto;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// M02's own field protection, keyed so it cannot collide with M01's.
///
/// <para>
/// <c>AddFieldProtection</c> takes a section name, which reads as "each module brings its own
/// keys". It does not: <see cref="FieldProtectionOptions"/> is one options instance for the
/// container and <see cref="IFieldEncryptor"/> one singleton, so a second call binds over the
/// first and both modules end up sharing whichever key registered last. Measured, not assumed —
/// and for M01 that would have meant client PII encrypted with one key and decrypted with
/// another, discovered the next time someone opened a client record. The shared helper now
/// throws on a second section; this is the way round it.
/// </para>
///
/// <para>
/// Keyed registration rather than a second unkeyed one: a KYC document number and a customer's
/// phone must not be decryptable with the same key, and the two modules must be able to rotate
/// independently.
/// </para>
/// </summary>
public static class KycFieldProtection
{
    /// <summary>DI key for this module's encryptor and indexer.</summary>
    public const string Key = "Kyc";

    /// <summary>Configuration section: <c>Kyc:FieldEncryptionKey</c>, <c>Kyc:BlindIndexKey</c>.</summary>
    public const string SectionName = "Kyc";

    public static IServiceCollection AddKycFieldProtection(
        this IServiceCollection services, IConfiguration config)
    {
        // Built here rather than through services.Configure: a named options instance would still
        // be reachable as the unnamed one by anything resolving IOptions<FieldProtectionOptions>,
        // and the whole point is that nothing outside this module ever sees these keys.
        var options = Options.Create(new FieldProtectionOptions
        {
            SectionName = SectionName,
            FieldEncryptionKey = config[$"{SectionName}:FieldEncryptionKey"]!,
            BlindIndexKey = config[$"{SectionName}:BlindIndexKey"]!,
        });

        // The key itself is validated lazily, on first use, by the primitives — a misconfigured
        // KYC key must not stop the whole API from booting for the modules that do not need it.
        services.AddKeyedSingleton<IFieldEncryptor>(Key, (_, _) => new AesGcmFieldEncryptor(options));
        services.AddKeyedSingleton<IBlindIndexer>(Key, (_, _) => new HmacBlindIndexer(options));

        return services;
    }
}

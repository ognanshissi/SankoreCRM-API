namespace Sankore.Modules.Leads.Infrastructure.Crypto;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// M13's own field protection, keyed so it cannot collide with M01's or M02's.
///
/// <para>
/// <b>Do not replace this with <c>AddFieldProtection(config, "Leads")</c>.</b> That helper takes a
/// section name, which reads as "each module brings its own keys"; it does not.
/// <see cref="FieldProtectionOptions"/> is one options instance for the container and
/// <see cref="IFieldEncryptor"/> one singleton, so a second call binds over the first and every
/// module ends up sharing whichever key registered last. M01 has already consumed the unkeyed
/// registration, so for M13 that would mean client PII encrypted with one key and decrypted with
/// another — discovered the next time somebody opened a client record. The shared helper now
/// throws on a second section; this is the way round it, and <c>KycFieldProtection</c> is the same
/// pattern for M02.
/// </para>
///
/// <para>
/// Keyed rather than a second unkeyed registration: a prospect's e-mail and a client's identity
/// document must not be decryptable with the same key, and the three modules have to be able to
/// rotate independently.
/// </para>
///
/// <para>
/// <b>What this does NOT replace.</b> <c>IPhoneBlindIndexer</c> / <c>HmacPhoneBlindIndexer</c> stay
/// exactly as they are. Their indexes are already in <c>leads.leads.phone_blind_index</c> and the
/// F13.2 duplicate detection reads them; recomputing those values under a different key would
/// silently stop every phone match from resolving.
/// </para>
/// </summary>
public static class LeadsFieldProtection
{
    /// <summary>DI key for this module's encryptor and indexer.</summary>
    public const string Key = "Leads";

    /// <summary>Configuration section: <c>Leads:FieldEncryptionKey</c>, <c>Leads:BlindIndexKey</c>.</summary>
    public const string SectionName = "Leads";

    public static IServiceCollection AddLeadsFieldProtection(
        this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        // Built here rather than through services.Configure: a named options instance would still
        // be reachable as the unnamed one by anything resolving IOptions<FieldProtectionOptions>,
        // and the whole point is that nothing outside this module ever sees these keys.
        var options = Options.Create(new FieldProtectionOptions
        {
            SectionName        = SectionName,
            FieldEncryptionKey = config[$"{SectionName}:FieldEncryptionKey"]!,
            BlindIndexKey      = config[$"{SectionName}:BlindIndexKey"]!,
        });

        // Validated lazily, on first use, by the primitives: a misconfigured M13 key must not stop
        // the whole API from booting for the modules that do not need it.
        services.AddKeyedSingleton<IFieldEncryptor>(Key, (_, _) => new AesGcmFieldEncryptor(options));
        services.AddKeyedSingleton<IBlindIndexer>(Key, (_, _) => new HmacBlindIndexer(options));

        return services;
    }
}

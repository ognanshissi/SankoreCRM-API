namespace Sankore.Modules.Administration.Infrastructure.Crypto;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// M12's own field protection, keyed so it cannot collide with M01's, M02's or M13's.
///
/// <para>
/// <b>Do not replace this with <c>AddFieldProtection(config, "Administration")</c>.</b>
/// <see cref="FieldProtectionOptions"/> is one options instance for the container and
/// <see cref="IFieldEncryptor"/> one singleton, so a second call binds over the first and every
/// module ends up sharing whichever key registered last. M01 has already consumed the unkeyed
/// registration; a second one would mean client PII encrypted with one key and decrypted with
/// another. The shared helper throws on a second section, and this is the way round it —
/// <c>LeadsFieldProtection</c> and <c>KycFieldProtection</c> are the same pattern.
/// </para>
///
/// <para>
/// <b>What it protects.</b> The professional card number of a distribution credential
/// (CIMA article 510): a licence number identifies a named person and appears on a regulator's
/// register, so it is held encrypted and compared through a blind index rather than read back.
/// Nothing else in this module is encrypted today — a user's e-mail is addressed, not protected,
/// and the compliance settings hold no personal data.
/// </para>
/// </summary>
public static class AdministrationFieldProtection
{
    /// <summary>DI key for this module's encryptor and indexer.</summary>
    public const string Key = "Administration";

    /// <summary>
    /// Configuration section: <c>Administration:FieldEncryptionKey</c>,
    /// <c>Administration:BlindIndexKey</c>.
    /// </summary>
    public const string SectionName = "Administration";

    public static IServiceCollection AddAdministrationFieldProtection(
        this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        // Built here rather than through services.Configure: a named options instance would still
        // be reachable as the unnamed one by anything resolving IOptions<FieldProtectionOptions>,
        // and the point is that nothing outside this module ever sees these keys.
        var options = Options.Create(new FieldProtectionOptions
        {
            SectionName        = SectionName,
            FieldEncryptionKey = config[$"{SectionName}:FieldEncryptionKey"]!,
            BlindIndexKey      = config[$"{SectionName}:BlindIndexKey"]!,
        });

        // Validated lazily, on first use, by the primitives: a misconfigured M12 key must not
        // stop the whole API from booting for the modules that do not need it. The first use is
        // a write of a professional card, so a tenant that records none never touches it.
        services.AddKeyedSingleton<IFieldEncryptor>(Key, (_, _) => new AesGcmFieldEncryptor(options));
        services.AddKeyedSingleton<IBlindIndexer>(Key, (_, _) => new HmacBlindIndexer(options));

        return services;
    }
}

namespace Sankore.Shared.Infrastructure.Crypto;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI registration for the field-level protection primitives. Both services are
/// stateless singletons; their keys are read lazily from
/// <see cref="FieldProtectionOptions"/> on first use.
/// </summary>
public static class CryptoServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="FieldProtectionOptions"/> from <paramref name="sectionName"/>
    /// (default <c>"Customers"</c> → <c>Customers:FieldEncryptionKey</c> and
    /// <c>Customers:BlindIndexKey</c>) and registers
    /// <see cref="IFieldEncryptor"/> + <see cref="IBlindIndexer"/>.
    /// </summary>
    public static IServiceCollection AddFieldProtection(
        this IServiceCollection services,
        IConfiguration config,
        string sectionName = "Customers")
    {
        // FieldProtectionOptions is ONE options instance for the whole container, and
        // IFieldEncryptor ONE singleton. Calling this twice with different sections does not give
        // each module its own key: both bind into the same instance, the last registration wins,
        // and every module silently shares it. For M01 that would mean client PII encrypted with
        // one key and decrypted with another the day a second module registers — unrecoverable
        // data, discovered in production.
        //
        // Until the keys are properly per-module (named options plus a keyed encryptor), refuse
        // the second section loudly rather than let it through.
        var alreadyBound = services.FirstOrDefault(d => d.ServiceType == typeof(FieldProtectionMarker));
        if (alreadyBound?.ImplementationInstance is FieldProtectionMarker marker)
        {
            if (!string.Equals(marker.SectionName, sectionName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"AddFieldProtection was already called for section \"{marker.SectionName}\" and is "
                    + $"now called for \"{sectionName}\". Both would bind the same "
                    + "FieldProtectionOptions and share a single IFieldEncryptor, so one module's "
                    + "data would be encrypted and decrypted with the other module's key. Give the "
                    + "second module its own options type and encryptor instance instead.");
            }

            return services;
        }

        services.AddSingleton(new FieldProtectionMarker(sectionName));
        services.Configure<FieldProtectionOptions>(sectionName, config.GetSection(sectionName));
        services.Configure<FieldProtectionOptions>(o =>
        {
            config.GetSection(sectionName).Bind(o);
            o.SectionName = sectionName;
        });
        services.AddSingleton<IFieldEncryptor, AesGcmFieldEncryptor>();
        services.AddSingleton<IBlindIndexer, HmacBlindIndexer>();
        return services;
    }

    /// <summary>Records which section the container already bound, so a second one is caught.</summary>
    private sealed record FieldProtectionMarker(string SectionName);
}

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
        services.Configure<FieldProtectionOptions>(config.GetSection(sectionName));
        services.AddSingleton<IFieldEncryptor, AesGcmFieldEncryptor>();
        services.AddSingleton<IBlindIndexer, HmacBlindIndexer>();
        return services;
    }
}

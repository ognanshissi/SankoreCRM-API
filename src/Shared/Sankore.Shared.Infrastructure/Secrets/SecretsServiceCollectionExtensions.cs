namespace Sankore.Shared.Infrastructure.Secrets;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sankore.Shared.Kernel;

/// <summary>
/// Registration and bootstrap of the secrets vault.
///
/// It exists for one reason: the vault has two prerequisites that used to be invisible until
/// something tried to use it — an encryption key, and its own applied migration. A missing key
/// surfaced as <c>ArgumentNullException (Parameter 's')</c> from
/// <c>Convert.FromBase64String</c> on the FIRST write, i.e. as a 500 for whoever happened to
/// configure an email provider, long after the deployment that caused it. A missing migration
/// surfaced as <c>relation "secrets.entries" does not exist</c> in the same place.
/// Both are now boot-time failures.
/// </summary>
public static class SecretsServiceCollectionExtensions
{
    /// <summary>Bytes an AES-256 key must decode to.</summary>
    private const int KeySizeBytes = 32;

    /// <param name="configureDb">
    /// Provider configuration for <see cref="SecretsDbContext"/>. Left to the caller because this
    /// assembly deliberately references no database provider — the bootstrapper owns Npgsql and
    /// the naming convention.
    /// </param>
    public static IServiceCollection AddSecretsVault(
        this IServiceCollection services,
        IConfiguration config,
        Action<DbContextOptionsBuilder> configureDb,
        string sectionName = "Secrets")
    {
        services.AddOptions<SecretsOptions>()
            .Bind(config.GetSection(sectionName))
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.EncryptionKey),
                $"{sectionName}:EncryptionKey is not configured. Generate one with "
                + "\"openssl rand -base64 32\" and set it through user-secrets or the environment. "
                + "Losing it makes every stored secret undecryptable — treat it as a backed-up key, "
                + "not a config value.")
            .Validate(
                o => string.IsNullOrWhiteSpace(o.EncryptionKey) || DecodedLength(o.EncryptionKey) >= 0,
                $"{sectionName}:EncryptionKey is not valid Base64.")
            .Validate(
                o => string.IsNullOrWhiteSpace(o.EncryptionKey)
                     || DecodedLength(o.EncryptionKey) is < 0 or KeySizeBytes,
                $"{sectionName}:EncryptionKey must decode to exactly {KeySizeBytes} bytes (AES-256).")
            .ValidateOnStart();

        services.AddDbContext<SecretsDbContext>(configureDb);
        services.AddScoped<ISecretsModule, AesSecretsModule>();

        return services;
    }

    /// <summary>
    /// Applies the vault's migration. Called from the bootstrapper's startup scope alongside the
    /// modules: the vault is shared infrastructure, so no module owns its schema and nobody was
    /// migrating it.
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider sp)
    {
        // Resolving the options here forces the key validation to run BEFORE any schema work,
        // so a misconfigured deployment fails on the key rather than half-way through migrations.
        _ = sp.GetRequiredService<IOptions<SecretsOptions>>().Value;

        var db = sp.GetRequiredService<SecretsDbContext>();
        await db.Database.MigrateAsync();
    }

    /// <summary>Decoded byte length, or -1 when the value is not Base64.</summary>
    private static int DecodedLength(string value)
    {
        var buffer = new byte[((value.Length * 3) / 4) + 4];
        return Convert.TryFromBase64String(value, buffer, out var written) ? written : -1;
    }
}

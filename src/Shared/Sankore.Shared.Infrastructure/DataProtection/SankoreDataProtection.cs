namespace Sankore.Shared.Infrastructure.DataProtection;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Persists the ASP.NET Data Protection key ring in PostgreSQL.
///
/// It exists because of a failure that is invisible until a user clicks a link. Identity's
/// <c>AddDefaultTokenProviders()</c> signs every account-activation and password-reset token with
/// the Data Protection key ring. Left unconfigured, that ring is written to
/// <c>~/.aspnet/DataProtection-Keys</c> inside the container: it is recreated from scratch on every
/// redeploy, and differs between replicas. Tokens issued before the restart then fail to unprotect,
/// so <c>GET auth/activate/verify</c> answers 400 "Activation link has expired or already been
/// used." for links that are neither expired nor used — and nothing in the logs points at the key
/// ring. Keeping the ring in the database makes activation links survive a deployment.
///
/// The application name is part of every payload's purpose chain, so it must stay STABLE across
/// deployments and identical on every host that unprotects these tokens. The default derives from
/// the content-root path, which is exactly the kind of value a container image changes silently —
/// hence the explicit constant.
/// </summary>
public static class SankoreDataProtection
{
    /// <summary>
    /// Discriminator baked into every protected payload. Changing it invalidates every activation
    /// link, reset link and other protected value already in circulation.
    /// </summary>
    public const string ApplicationName = "SankoreCRM";

    /// <param name="configureDb">
    /// Provider configuration for <see cref="DataProtectionKeysDbContext"/>. Left to the caller
    /// because this assembly deliberately references no database provider — the bootstrapper owns
    /// Npgsql and the naming convention.
    /// </param>
    public static IServiceCollection AddSankoreDataProtection(
        this IServiceCollection services,
        IConfiguration config,
        Action<DbContextOptionsBuilder> configureDb,
        string sectionName = "DataProtection")
    {
        services.AddDbContext<DataProtectionKeysDbContext>(configureDb);

        var builder = services.AddDataProtection()
            .SetApplicationName(config[$"{sectionName}:ApplicationName"] ?? ApplicationName)
            .PersistKeysToDbContext<DataProtectionKeysDbContext>();

        // Key lifetime is the ring's own rotation period, not a token lifespan: keys stay usable
        // for decryption long after they stop being used for encryption, so raising it does not
        // extend how long an activation link is valid.
        if (int.TryParse(config[$"{sectionName}:KeyLifetimeDays"], out var lifetimeDays)
            && lifetimeDays > 0)
        {
            builder.SetDefaultKeyLifetime(TimeSpan.FromDays(lifetimeDays));
        }

        return services;
    }

    /// <summary>
    /// Creates the <c>dataprotection.keys</c> table. Called from the bootstrapper's startup scope
    /// alongside the audit and secrets schemas: like those, the key ring is shared infrastructure
    /// that no module would otherwise migrate.
    ///
    /// Must run BEFORE anything protects or unprotects a payload. Data Protection swallows a
    /// missing-table read and silently falls back to generating an in-memory key, which looks like
    /// it works until the next restart.
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<DataProtectionKeysDbContext>();
        await db.Database.MigrateAsync();
    }
}

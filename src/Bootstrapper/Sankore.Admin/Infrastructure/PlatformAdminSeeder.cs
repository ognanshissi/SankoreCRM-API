using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;

namespace Sankore.Admin.Infrastructure;

/// <summary>The first platform operator, read from <c>PlatformAdmin</c> in configuration.</summary>
public sealed class PlatformAdminSeedOptions
{
    public const string Section = "PlatformAdmin";
    public const int MinPasswordLength = 12;

    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = "Administrateur plateforme";

    /// <summary>Never in a committed file: user-secrets locally, the environment elsewhere.</summary>
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Creates the first platform operator when the table is EMPTY, and does nothing otherwise.
///
/// <para>
/// "Empty" and not "this email is missing": the seed is how a fresh deployment gets in, not a way
/// to manage accounts. Keyed on the email, a typo in the variable would mint a second operator on
/// the next deploy; and an existing account's password is never rewritten from configuration, or
/// whoever can edit an environment variable could take over the registry by restarting it.
/// </para>
///
/// <para>Like <see cref="TenantSeeder"/>, nothing here throws: it logs the reason and the remedy.</para>
/// </summary>
internal static class PlatformAdminSeeder
{
    public static async Task SeedAsync(
        AdminDbContext db,
        PlatformAdminSeedOptions options,
        IPasswordHasher<PlatformAdmin> hasher,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (await db.PlatformAdmins.AnyAsync(ct))
            return;

        if (string.IsNullOrWhiteSpace(options.Email) || string.IsNullOrWhiteSpace(options.Password))
        {
            logger.LogWarning(
                "No platform administrator exists and none is configured, so nobody can sign in to "
                + "the admin front-end. Set {Section}__Email and {Section}__Password (locally: "
                + "dotnet user-secrets set \"{Section}:Password\" \"...\" --project src/Bootstrapper/Sankore.Admin) "
                + "and restart.",
                PlatformAdminSeedOptions.Section, PlatformAdminSeedOptions.Section, PlatformAdminSeedOptions.Section);
            return;
        }

        if (options.Password.Length < PlatformAdminSeedOptions.MinPasswordLength)
        {
            logger.LogError(
                "{Section}:Password is shorter than {Min} characters; the first administrator was not created.",
                PlatformAdminSeedOptions.Section, PlatformAdminSeedOptions.MinPasswordLength);
            return;
        }

        var admin = PlatformAdmin.Create(options.Email, options.FullName, passwordHash: string.Empty);
        admin.ReplacePasswordHash(hasher.HashPassword(admin, options.Password));

        db.PlatformAdmins.Add(admin);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "PlatformAdminSeeder: created the first platform administrator {AdminId} ({Email}). "
            + "Remove {Section}__Password from the environment now that it is stored hashed.",
            admin.Id, admin.Email, PlatformAdminSeedOptions.Section);
    }
}

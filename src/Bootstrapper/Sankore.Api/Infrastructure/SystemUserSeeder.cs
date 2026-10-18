using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Users.Register;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;

namespace Sankore.Api.Infrastructure;

/// <summary>
/// Provisions the tenant's system user at start-up, from <see cref="SystemUserOptions"/>, so a
/// fresh deployment does not need the manual <c>POST /api/v1/users/create-root</c> described in
/// docs/deployment-dokploy.md before anybody can log in.
///
/// <para>
/// It sends the SAME <see cref="RegisterCommand"/> that endpoint sends rather than writing the
/// user itself: the handler also provisions the user's profile, the tenant's CompanyInfo and the
/// <c>UserCreatedEvent</c> on the outbox, and a second copy of that would drift.
/// </para>
///
/// <para>
/// Nothing here throws. Every giving-up path logs the reason and the manual remedy, and the host
/// finishes starting: an API that refuses to boot because a registry was slow answers nothing at
/// all — not even <c>/health</c> — and in Dokploy that is a restart loop instead of a diagnosable
/// deployment.
/// </para>
/// </summary>
internal static class SystemUserSeeder
{
    // Sankore.Admin migrates and seeds its own tenant while this host is starting, and the two
    // containers come up together. Negative lookups are NOT cached (CachedTenantStore), so
    // retrying is both safe and the whole point — without it, whichever host won the race decided
    // whether the deployment had a system user.
    private const int DefaultMaxAttempts = 10;
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(3);

    /// <param name="maxAttempts">Overridden only by the tests, which must not wait the real
    /// half-minute to assert that giving up does not throw.</param>
    /// <param name="retryDelay">Same.</param>
    public static async Task SeedAsync(
        IServiceProvider services,
        int maxAttempts = DefaultMaxAttempts,
        TimeSpan? retryDelay = null,
        CancellationToken ct = default)
    {
        var options = services.GetRequiredService<IOptions<SystemUserOptions>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(SystemUserSeeder));

        if (options.IsDisabled)
        {
            logger.LogInformation(
                "No system user configured ({Section}__Password is empty), so none was "
                + "provisioned. Create one with POST /api/v1/users/create-root when needed.",
                SystemUserOptions.Section);
            return;
        }

        if (!TenantFqdn.IsOrigin(options.TenantFqdn, out var problem))
        {
            logger.LogError(
                "System user not provisioned: {Section}__TenantFqdn {Problem}",
                SystemUserOptions.Section, problem);
            return;
        }

        var fqdn = TenantFqdn.Normalize(options.TenantFqdn);

        var tenant = await ResolveTenantAsync(
            services, fqdn, logger, maxAttempts, retryDelay ?? DefaultRetryDelay, ct);
        if (tenant is null)
            return;

        // The ambient tenant scope MUST be established before the DI scope is created:
        // ITenantContext is scoped and resolves from BackgroundJobContext at construction
        // (Program.cs), so a scope built first would get the HTTP implementation with no
        // HttpContext behind it. Guid.Empty as the actor, like the Hangfire jobs do — the user
        // being created cannot be its own author.
        using var ambient = BackgroundJobContext.SetScope(tenant.Id, Guid.Empty, "SYSTEM");
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        var db = sp.GetRequiredService<AdministrationDbContext>();

        // Same predicate as RegisterHandler's own guard, run here so a redeploy is a no-op that
        // logs rather than a command that fails. IgnoreQueryFilters because the filter would
        // scope the query to the tenant anyway and this must be unambiguous.
        var alreadyProvisioned = await db.Users
            .IgnoreQueryFilters()
            .AnyAsync(
                u => u.TenantId == tenant.Id
                     && u.IsSuperUser
                     && u.AccountType == UserAccountType.System,
                ct);

        if (alreadyProvisioned)
        {
            logger.LogInformation(
                "SystemUserSeeder: tenant {TenantId} ({Fqdn}) already has its system user",
                tenant.Id, fqdn);
            return;
        }

        var email = string.IsNullOrWhiteSpace(options.Email)
            ? await ResolveTenantEmailAsync(services, fqdn, ct)
            : options.Email.Trim();

        if (string.IsNullOrWhiteSpace(email))
        {
            logger.LogError(
                "System user not provisioned: no address. Set {Section}__Email, or give the "
                + "tenant a RootUserEmail in the registry.",
                SystemUserOptions.Section);
            return;
        }

        if (!PasswordMeetsPolicy(sp, options.Password, out var policyProblem))
        {
            // Checked here rather than left to UserManager: its failure surfaces as a list of
            // IdentityError descriptions inside a Result, which reads like a bug in the seeder
            // instead of a setting to fix.
            logger.LogError(
                "System user not provisioned: {Section}__Password {Problem}",
                SystemUserOptions.Section, policyProblem);
            return;
        }

        var result = await sp.GetRequiredService<ISender>().Send(
            new RegisterCommand(
                Email: email,
                Password: options.Password,
                ConfirmPassword: options.Password,
                FirstName: options.FirstName,
                LastName: options.LastName,
                TenantId: tenant.Id),
            ct);

        if (!result.IsSuccess)
        {
            logger.LogError(
                "SystemUserSeeder: could not provision the system user of tenant {TenantId}: "
                + "{Error}. Provision it by hand with POST /api/v1/users/create-root.",
                tenant.Id, result.Error);
            return;
        }

        logger.LogInformation(
            "SystemUserSeeder: created system user {UserId} ({Email}) for tenant {TenantId}",
            result.Value.UserId, email, tenant.Id);
    }

    private static async Task<TenantInfo?> ResolveTenantAsync(
        IServiceProvider services,
        string fqdn,
        ILogger logger,
        int maxAttempts,
        TimeSpan retryDelay,
        CancellationToken ct)
    {
        var tenantStore = services.GetRequiredService<ITenantStore>();

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var tenant = await tenantStore.GetByFqdnAsync(fqdn, ct);
            if (tenant is not null)
                return tenant;

            if (attempt < maxAttempts)
            {
                logger.LogInformation(
                    "SystemUserSeeder: tenant {Fqdn} not in the registry yet "
                    + "(attempt {Attempt}/{MaxAttempts}), retrying in {Delay}s",
                    fqdn, attempt, maxAttempts, retryDelay.TotalSeconds);

                await Task.Delay(retryDelay, ct);
            }
        }

        logger.LogError(
            "SystemUserSeeder: tenant {Fqdn} is still unknown to the registry after "
            + "{MaxAttempts} attempts, so no system user was provisioned. Check that "
            + "Sankore.Admin is reachable at TenantStore__BaseUrl, that DEFAULT_TENANT_FQDN "
            + "matches this origin exactly, then provision by hand with "
            + "POST /api/v1/users/create-root.",
            fqdn, maxAttempts);

        return null;
    }

    /// <summary>
    /// The registry's full record carries the tenant's own root address, which is the sensible
    /// default for the account being created. <see cref="TenantInfo"/> does not expose it, so it
    /// is read from the same endpoint the store wraps.
    /// </summary>
    private static async Task<string?> ResolveTenantEmailAsync(
        IServiceProvider services, string fqdn, CancellationToken ct)
    {
        var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("TenantStore");

        try
        {
            var response = await client.GetAsync($"api/v1/tenants/by-fqdn?domain={Uri.EscapeDataString(fqdn)}", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            using var document = await System.Text.Json.JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            return document.RootElement.TryGetProperty("rootUserEmail", out var value)
                ? value.GetString()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Validates against the policy Identity is ACTUALLY configured with
    /// (<c>AdministrationModule.AddAdministrationModule</c> sets a length and a digit and leaves
    /// the rest at their defaults), read from the container rather than restated here — a second
    /// copy would quietly stop matching the first.
    /// </summary>
    private static bool PasswordMeetsPolicy(
        IServiceProvider sp, string password, out string? problem)
    {
        var policy = sp.GetRequiredService<IOptions<IdentityOptions>>().Value.Password;
        var missing = new List<string>();

        if (password.Length < policy.RequiredLength)
            missing.Add($"at least {policy.RequiredLength} characters");
        if (policy.RequireDigit && !password.Any(char.IsDigit))
            missing.Add("a digit");
        if (policy.RequireLowercase && !password.Any(char.IsLower))
            missing.Add("a lower-case letter");
        if (policy.RequireUppercase && !password.Any(char.IsUpper))
            missing.Add("an upper-case letter");
        if (policy.RequireNonAlphanumeric && password.All(char.IsLetterOrDigit))
            missing.Add("a non-alphanumeric character");
        if (password.Distinct().Count() < policy.RequiredUniqueChars)
            missing.Add($"{policy.RequiredUniqueChars} distinct characters");

        problem = missing.Count == 0
            ? null
            : "does not satisfy the password policy: it needs " + string.Join(", ", missing) + ".";

        return missing.Count == 0;
    }
}

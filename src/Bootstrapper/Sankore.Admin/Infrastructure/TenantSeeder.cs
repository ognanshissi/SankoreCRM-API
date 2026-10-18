using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;

namespace Sankore.Admin.Infrastructure;

/// <summary>
/// Provisions the default tenant and its primary domain at start-up, from
/// <see cref="DefaultTenantOptions"/>.
///
/// <para>
/// Idempotent, and keyed on the configured id when there is one and on the origin otherwise, so
/// a redeploy writes nothing. It used to be keyed on "no tenant exists at all" with the values
/// hard-coded: a deployment got a registry holding one unusable <c>http://localhost:4222</c>
/// tenant, and that row then blocked every later seed.
/// </para>
///
/// <para>
/// Nothing here throws. A registry that refuses to start is worse than one holding no tenant:
/// the manual route — <c>POST /api/v1/tenants</c> with the service key — stays available, and it
/// needs the host to be up. Every giving-up path logs the reason and the remedy.
/// </para>
/// </summary>
internal static class TenantSeeder
{
    // Development fallback, the values this seeder used to hard-code: the front-end's dev origin
    // and a throwaway address, so `dotnet run` still yields a usable registry with no
    // configuration at all. Outside Development, missing configuration seeds nothing.
    private static readonly Guid DevTenantId = new("2fae736d-9c5c-456e-8d3c-5e4e9b0674ce");
    private const string DevFqdn = "http://localhost:4222";
    private const string DevEmail = "admin@sankore.dev";
    private const string DevName = "Sankore Dev";

    public static async Task SeedAsync(
        AdminDbContext db,
        DefaultTenantOptions options,
        IHostEnvironment env,
        ILogger logger,
        CancellationToken ct = default)
    {
        var fqdn = DefaultTenantOptions.NormalizeFqdn(options.Fqdn);
        var email = options.RootUserEmail.Trim();
        var name = string.IsNullOrWhiteSpace(options.Name)
            ? DefaultTenantOptions.DefaultName
            : options.Name.Trim();
        var pinnedId = options.ResolvedId;

        if (options.IsEmpty)
        {
            if (!env.IsDevelopment())
            {
                logger.LogWarning(
                    "No default tenant configured, so none was provisioned. Set "
                    + "{Section}__Fqdn (the front-end's origin, scheme included) and "
                    + "{Section}__RootUserEmail, or create the tenant with POST /api/v1/tenants.",
                    DefaultTenantOptions.Section, DefaultTenantOptions.Section);
                return;
            }

            (fqdn, email, name, pinnedId) = (DevFqdn, DevEmail, DevName, DevTenantId);
        }
        else if (!options.Validate(out var error))
        {
            logger.LogError("Default tenant not provisioned: {Error}", error);
            return;
        }

        // By id first when one is pinned: that is what makes a domain migration a change of one
        // variable rather than a second tenant with a new id — which would orphan every row of
        // the API's database, all of it keyed by this id.
        var tenant = pinnedId is { } id
            ? await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct)
            : null;

        tenant ??= await db.Tenants.FirstOrDefaultAsync(t => t.Fqdn == fqdn, ct);

        if (tenant is not null)
        {
            await UpdateExistingAsync(db, tenant, fqdn, pinnedId, logger, ct);
            return;
        }

        tenant = Tenant.Create(name: name, rootUserEmail: email, fqdn: fqdn);

        if (pinnedId is { } pinned)
            SetId(tenant, pinned);

        // Not persisted — see DefaultTenantOptions.Modules.
        tenant.ApplicationModulesList.AddRange(options.Modules);

        db.Tenants.Add(tenant);
        db.TenantDomains.Add(TenantDomain.Create(tenant.Id, fqdn, isPrimary: true));

        await db.SaveChangesAsync(ct);

        // Information, and deliberately: this id is what an operator needs for every later call,
        // and reading it from the logs beats opening the database.
        logger.LogInformation(
            "TenantSeeder: provisioned tenant {TenantId} ({TenantName}) for {Fqdn}",
            tenant.Id, tenant.Name, fqdn);
    }

    private static async Task UpdateExistingAsync(
        AdminDbContext db,
        Tenant tenant,
        string fqdn,
        Guid? pinnedId,
        ILogger logger,
        CancellationToken ct)
    {
        // The name and the address of an existing tenant are never rewritten from configuration:
        // a typo in an environment variable must not rename a live tenant, and root_user_email
        // carries a unique index that would turn the attempt into a start-up failure.
        if (!string.Equals(tenant.Fqdn, fqdn, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "TenantSeeder: tenant {TenantId} moves from {OldFqdn} to {NewFqdn}. Clients "
                + "calling the old origin will no longer resolve.",
                tenant.Id, tenant.Fqdn, fqdn);

            tenant.Update(tenant.Name, fqdn);

            var previousPrimaries = await db.TenantDomains
                .Where(d => d.TenantId == tenant.Id && d.IsPrimary)
                .ToListAsync(ct);

            foreach (var domain in previousPrimaries)
                domain.UnsetPrimary();
        }
        else if (pinnedId is { } pinned && pinned != tenant.Id)
        {
            // Found by origin, not by id, and the configured id is some other value. The id is
            // the primary key and the API's data is keyed by it, so the existing one wins — but
            // an operator who pinned the wrong id has to be told, or they will believe it took.
            logger.LogWarning(
                "TenantSeeder: {Section}__Id is {ConfiguredId}, but {Fqdn} already belongs to "
                + "tenant {TenantId}. The existing id is kept.",
                DefaultTenantOptions.Section, pinned, fqdn, tenant.Id);
        }

        // Self-heal: the row is cosmetic today (no resolution path reads tenant_domains, they all
        // match tenants.fqdn), but its absence would surprise anyone reading the registry.
        var hasDomain = await db.TenantDomains.AnyAsync(d => d.Fqdn == fqdn, ct);
        if (!hasDomain)
            db.TenantDomains.Add(TenantDomain.Create(tenant.Id, fqdn, isPrimary: true));

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "TenantSeeder: tenant {TenantId} already provisioned for {Fqdn}", tenant.Id, fqdn);
    }

    // Sets the private Id property via reflection — used only here to pin the seed tenant ID.
    private static void SetId(Tenant tenant, Guid id)
    {
        var prop = typeof(Tenant).GetProperty(nameof(Tenant.Id))!;
        prop.SetValue(tenant, id);
    }
}

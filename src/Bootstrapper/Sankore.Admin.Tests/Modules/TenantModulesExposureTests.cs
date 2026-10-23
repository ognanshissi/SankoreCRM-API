namespace Sankore.Admin.Tests.Modules;

using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Admin.Domain;
using Sankore.Admin.Features.Tenants.CreateTenant;
using Sankore.Admin.Features.Tenants.GetTenant;
using Sankore.Admin.Features.Tenants.GetTenantByFqdn;
using Sankore.Admin.Features.Tenants.ListTenants;
using Sankore.Admin.Infrastructure;
using Sankore.Admin.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// How modules leave the registry: on TenantResponse (which Sankore.Api deserialises into
/// TenantInfo), at tenant creation, at seeding — and who may take a platform decision.
/// </summary>
public sealed class TenantModulesExposureTests : IDisposable
{
    private readonly ModuleTestDb _store = new();

    public void Dispose() => _store.Dispose();

    private async Task GrantAsync(string module, ModuleSubscriptionStatus finalStatus = ModuleSubscriptionStatus.Active)
    {
        await using var db = _store.Create();
        var row = TenantModuleSubscription.ActivateDirect(_store.TenantId, module, null,
            ModuleSubscriptionSource.Direct, DateTimeOffset.UtcNow);
        if (finalStatus == ModuleSubscriptionStatus.Deactivated)
            row.Deactivate(Guid.NewGuid(), "a@b.c", DateTimeOffset.UtcNow);
        db.TenantModuleSubscriptions.Add(row);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_three_lookups_expose_the_same_active_modules_and_only_active_ones()
    {
        await GrantAsync(TenantModules.Insurance);
        await GrantAsync(TenantModules.BankingMicrofinance, ModuleSubscriptionStatus.Deactivated);

        await using var db = _store.Create();
        var byId = await new GetTenantHandler(db).Handle(new GetTenantQuery(_store.TenantId), default);
        var byFqdn = await new GetTenantByFqdnHandler(db).Handle(new GetTenantByFqdnQuery("https://crm.caisse.ci"), default);
        var listed = (await new ListTenantsHandler(db).Handle(new ListTenantsQuery(true), default)).Single();

        byId!.ActiveModules.Should().Equal(TenantModules.Insurance);
        byFqdn!.ActiveModules.Should().Equal(byId.ActiveModules);
        listed.ActiveModules.Should().Equal(byId.ActiveModules);

        byId.ReadOnlyModules.Should().Equal(TenantModules.BankingMicrofinance);
        byFqdn.ReadOnlyModules.Should().Equal(byId.ReadOnlyModules);
        listed.ReadOnlyModules.Should().Equal(byId.ReadOnlyModules);
    }

    [Fact]
    public async Task A_module_active_again_is_no_longer_read_only()
    {
        await GrantAsync(TenantModules.Insurance, ModuleSubscriptionStatus.Deactivated);
        await GrantAsync(TenantModules.Insurance);

        await using var db = _store.Create();
        var tenant = await new GetTenantHandler(db).Handle(new GetTenantQuery(_store.TenantId), default);

        tenant!.ActiveModules.Should().Equal(TenantModules.Insurance);
        tenant.ReadOnlyModules.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tenant_without_modules_exposes_an_empty_list_not_a_missing_one()
    {
        await using var db = _store.Create();
        var tenant = await new GetTenantHandler(db).Handle(new GetTenantQuery(_store.TenantId), default);

        // Empty means « socle only ». Sankore.Api reads a MISSING list as « every module » (an old
        // cached TenantInfo), so the registry must always send the field.
        tenant!.ActiveModules.Should().NotBeNull().And.BeEmpty();
        tenant.ReadOnlyModules.Should().NotBeNull().And.BeEmpty("a module never subscribed is not readable");
    }

    [Fact]
    public async Task A_creation_profile_switches_its_modules_on_directly()
    {
        var admin = Guid.NewGuid();
        await using var db = _store.Create();

        var created = await new CreateTenantHandler(db).Handle(new CreateTenantCommand(
            "Mutuelle", "root@mutuelle.ci", "https://crm.mutuelle.ci", null,
            [TenantModules.BankingMicrofinance, TenantModules.Insurance, TenantModules.Insurance], admin), default);

        var rows = await db.TenantModuleSubscriptions.Where(s => s.TenantId == created.Id).ToListAsync();
        rows.Should().HaveCount(2, "duplicates are ignored");
        rows.Should().OnlyContain(r => r.Status == ModuleSubscriptionStatus.Active
                                       && r.Source == ModuleSubscriptionSource.Direct
                                       && r.DecidedByAdminId == admin);
    }

    [Fact]
    public async Task A_tenant_created_without_modules_runs_on_the_socle_alone()
    {
        await using var db = _store.Create();
        var created = await new CreateTenantHandler(db).Handle(new CreateTenantCommand(
            "Socle", "root@socle.ci", "https://crm.socle.ci", null), default);

        (await db.TenantModuleSubscriptions.AnyAsync(s => s.TenantId == created.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task The_seeder_grants_modules_on_creation_and_never_again()
    {
        var options = new DefaultTenantOptions
        {
            Fqdn = "https://crm.seed.ci",
            RootUserEmail = "root@seed.ci",
            ActiveModules = "Insurance, Telephony",
        };
        await using var db = _store.Create();

        await TenantSeeder.SeedAsync(db, options, new Env(), NullLogger.Instance);
        var tenantId = (await db.Tenants.SingleAsync(t => t.Fqdn == "https://crm.seed.ci")).Id;
        var rows = await db.TenantModuleSubscriptions.Where(s => s.TenantId == tenantId).ToListAsync();
        rows.Select(r => r.ModuleCode).Should().Equal(TenantModules.Insurance);

        rows.Single().Deactivate(Guid.NewGuid(), "a@b.c", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        await TenantSeeder.SeedAsync(db, options, new Env(), NullLogger.Instance);
        (await db.TenantModuleSubscriptions.Where(s => s.TenantId == tenantId).ToListAsync())
            .Should().ContainSingle().Which.Status.Should().Be(ModuleSubscriptionStatus.Deactivated,
                "a redeploy must not switch back on a module the tenant switched off");
    }

    [Fact]
    public void The_seeded_tenant_receives_both_modules_by_default()
    {
        new DefaultTenantOptions().ActiveModuleCodes.Should().Equal(TenantModules.All);
        new DefaultTenantOptions { ActiveModules = "" }.ActiveModuleCodes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_platform_decision_is_refused_to_the_service_key_and_allowed_to_an_operator()
    {
        var reached = false;
        EndpointFilterDelegate next = _ => { reached = true; return ValueTask.FromResult<object?>(Results.NoContent()); };

        // The service key authenticates no principal: this is how Sankore.Api arrives.
        var keyCaller = new DefaultHttpContext();
        var refused = await PlatformAdminEndpointFilter.Gate(new DefaultEndpointFilterInvocationContext(keyCaller), next);
        reached.Should().BeFalse();
        refused.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

        var operatorCaller = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(AdminClaims.Subject, Guid.NewGuid().ToString()),
                new Claim(AdminClaims.Scope, AdminClaims.PlatformAdminScope),
            ], JwtBearerDefaults.AuthenticationScheme)),
        };
        await PlatformAdminEndpointFilter.Gate(new DefaultEndpointFilterInvocationContext(operatorCaller), next);
        reached.Should().BeTrue();
    }

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Sankore.Admin.Tests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

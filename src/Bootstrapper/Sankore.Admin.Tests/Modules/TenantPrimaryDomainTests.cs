namespace Sankore.Admin.Tests.Modules;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Features.Tenants.CreateTenant;
using Sankore.Admin.Features.Tenants.UpdateTenant;
using Xunit;

/// <summary>
/// A tenant's main origin is in its own « Domaines » list (2026-11-09): a tenant created from the
/// platform had no domain row for it, only the start-up seeder wrote one.
/// </summary>
public sealed class TenantPrimaryDomainTests : IDisposable
{
    private readonly ModuleTestDb _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task A_created_tenant_lists_its_main_origin_as_primary()
    {
        await using var db = _store.Create();

        var created = await new CreateTenantHandler(db).Handle(new CreateTenantCommand(
            "Démo Activité", "root@demo.ci", "http://Demo-Activite.localhost:4222/", null), default);

        (await db.Tenants.SingleAsync(t => t.Id == created.Id)).Fqdn.Should().Be("http://demo-activite.localhost:4222");
        var domains = await db.TenantDomains.Where(d => d.TenantId == created.Id).ToListAsync();
        domains.Should().ContainSingle();
        domains[0].Fqdn.Should().Be("http://demo-activite.localhost:4222");
        domains[0].IsPrimary.Should().BeTrue();
        domains[0].IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task An_origin_that_is_another_tenants_secondary_domain_is_refused_at_creation()
    {
        await using (var seed = _store.Create())
        {
            seed.TenantDomains.Add(TenantDomain.Create(_store.TenantId, "https://tenant-1.sankoreio.local:4222", isPrimary: false));
            await seed.SaveChangesAsync();
        }

        await using var db = _store.Create();
        var act = () => new CreateTenantHandler(db).Handle(new CreateTenantCommand(
            "Autre", "root@autre.ci", "https://tenant-1.sankoreio.local:4222", null), default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_new_main_origin_becomes_primary_and_the_old_one_stays_as_secondary()
    {
        await using (var seed = _store.Create())
        {
            seed.TenantDomains.Add(TenantDomain.Create(_store.TenantId, "https://crm.caisse.ci", isPrimary: true));
            await seed.SaveChangesAsync();
        }

        await using var db = _store.Create();
        var found = await new UpdateTenantHandler(db).Handle(new UpdateTenantCommand(
            _store.TenantId, "Caisse Avenir", "https://crm2.caisse.ci", IsActive: true, IsMaintenance: false), default);

        found.Should().BeTrue();
        var domains = await db.TenantDomains.Where(d => d.TenantId == _store.TenantId).ToListAsync();
        domains.Should().HaveCount(2);
        domains.Single(d => d.IsPrimary).Fqdn.Should().Be("https://crm2.caisse.ci");
        domains.Single(d => !d.IsPrimary).Fqdn.Should().Be("https://crm.caisse.ci");
    }

    [Fact]
    public async Task An_update_to_an_origin_another_tenant_holds_is_refused()
    {
        await using (var seed = _store.Create())
        {
            var other = Tenant.Create("Autre", "root@autre.ci", "https://crm.autre.ci");
            seed.Tenants.Add(other);
            await seed.SaveChangesAsync();
        }

        await using var db = _store.Create();
        var act = () => new UpdateTenantHandler(db).Handle(new UpdateTenantCommand(
            _store.TenantId, "Caisse Avenir", "https://crm.autre.ci", IsActive: true, IsMaintenance: false), default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}

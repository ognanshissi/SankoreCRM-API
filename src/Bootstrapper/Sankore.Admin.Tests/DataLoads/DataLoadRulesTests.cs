namespace Sankore.Admin.Tests.DataLoads;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Features.Tenants.CreateTenant;
using Sankore.Admin.Features.Tenants.DataLoads;
using Sankore.Admin.Tests.Modules;
using Xunit;

/// <summary>
/// Starting data (docs/donnees-demarrage-demo.md, 2026-11-08): the starter for any institution, the
/// demo only for a test one, never for a « Mode test » twin, one load at a time.
/// </summary>
public sealed class DataLoadRulesTests : IDisposable
{
    private readonly ModuleTestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<(string? Error, TenantDataLoad? Load)> RequestAsync(string kind, Guid? tenantId = null)
    {
        await using var db = _db.Create();
        return await DataLoadRules.RequestAsync(db, tenantId ?? _db.TenantId, kind, Guid.NewGuid(), DateTimeOffset.UtcNow, default);
    }

    [Fact]
    public async Task A_real_institution_gets_the_starter_and_never_the_demo()
    {
        (await RequestAsync("Demo")).Error.Should().Be(DataLoadEndpoints.DemoNeedsTest);

        var (error, load) = await RequestAsync("starter");
        error.Should().BeNull();
        load!.Kind.Should().Be(TenantDataLoadKind.Starter);
        load.Status.Should().Be(TenantDataLoadStatus.Pending);
    }

    [Fact]
    public async Task A_test_institution_gets_the_demo()
    {
        await using (var db = _db.Create())
        {
            (await db.Tenants.SingleAsync(t => t.Id == _db.TenantId)).MarkAsTest(true);
            await db.SaveChangesAsync();
        }

        (await RequestAsync("Demo")).Load!.Kind.Should().Be(TenantDataLoadKind.Demo);
    }

    [Fact]
    public async Task A_test_twin_gets_nothing_its_team_is_production()
    {
        Guid twinId;
        await using (var db = _db.Create())
        {
            var twin = Tenant.CreateTestTwin(await db.Tenants.SingleAsync(t => t.Id == _db.TenantId));
            db.Tenants.Add(twin);
            await db.SaveChangesAsync();
            twinId = twin.Id;
        }

        (await RequestAsync("Demo", twinId)).Error.Should().Be(DataLoadEndpoints.Twin);
        (await RequestAsync("Starter", twinId)).Error.Should().Be(DataLoadEndpoints.Twin);
    }

    [Fact]
    public async Task One_load_at_a_time_and_an_unknown_kind_is_refused()
    {
        (await RequestAsync("Starter")).Error.Should().BeNull();
        (await RequestAsync("Starter")).Error.Should().Be(DataLoadEndpoints.InFlight);
        (await RequestAsync("Everything")).Error.Should().Be(DataLoadEndpoints.KindUnknown);
    }

    [Fact]
    public async Task Creating_a_tenant_with_the_demo_marks_it_as_a_test_one_and_queues_the_load()
    {
        await using var db = _db.Create();
        var created = await new CreateTenantHandler(db).Handle(new CreateTenantCommand(
            "Démo Caisse", "demo@sankore.test", "https://demo.sankore.test", null,
            StartingData: TenantDataLoadKind.Demo), default);

        await using var read = _db.Create();
        (await read.Tenants.SingleAsync(t => t.Id == created.Id)).IsTest.Should().BeTrue();
        (await read.TenantDataLoads.SingleAsync(l => l.TenantId == created.Id))
            .Should().Match<TenantDataLoad>(l => l.Kind == TenantDataLoadKind.Demo && l.Status == TenantDataLoadStatus.Pending);
    }

    [Fact]
    public async Task Creating_a_tenant_without_starting_data_queues_nothing()
    {
        await using var db = _db.Create();
        var created = await new CreateTenantHandler(db).Handle(new CreateTenantCommand(
            "Caisse Neuve", "root@neuve.ci", "https://crm.neuve.ci", null), default);

        await using var read = _db.Create();
        (await read.TenantDataLoads.AnyAsync(l => l.TenantId == created.Id)).Should().BeFalse();
        (await read.Tenants.SingleAsync(t => t.Id == created.Id)).IsTest.Should().BeFalse();
    }
}

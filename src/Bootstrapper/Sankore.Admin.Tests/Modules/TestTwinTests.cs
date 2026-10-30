namespace Sankore.Admin.Tests.Modules;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Admin.Domain;
using Sankore.Admin.Features.Tenants.GetTenant;
using Sankore.Admin.Features.Tenants.GetTenantByFqdn;
using Sankore.Admin.Features.Tenants.ListTenants;
using Sankore.Admin.Features.Tenants.TestTwin;
using Sankore.Admin.Features.Tenants.SetTenantActivation;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Sandbox lot 2 at the registry: a tenant's « Mode test » twin is created once, answers with
/// its parent's modules and state, and never shows up where institutions are listed or resolved.
/// </summary>
public sealed class TestTwinTests : IDisposable
{
    private readonly ModuleTestDb _store = new();

    public void Dispose() => _store.Dispose();

    private async Task<Guid> EnsureAsync(Guid? tenantId = null)
    {
        await using var db = _store.Create();
        return (await new EnsureTestTwinHandler(db, NullLogger<EnsureTestTwinHandler>.Instance)
            .Handle(new EnsureTestTwinCommand(tenantId ?? _store.TenantId), default))!.Value;
    }

    private async Task<TenantResponse> GetAsync(Guid id)
    {
        await using var db = _store.Create();
        return (await new GetTenantHandler(db).Handle(new GetTenantQuery(id), default))!;
    }

    [Fact]
    public async Task The_twin_is_created_once_and_both_sides_name_each_other()
    {
        var twin = await EnsureAsync();
        (await EnsureAsync()).Should().Be(twin, "a second switch answers the same twin");

        var parent = await GetAsync(_store.TenantId);
        var child = await GetAsync(twin);

        parent.TestTenantId.Should().Be(twin);
        parent.ParentTenantId.Should().BeNull();
        child.ParentTenantId.Should().Be(_store.TenantId);
        child.IsTest.Should().BeTrue();
        child.Fqdn.Should().Be(parent.Fqdn, "the links a twin mails must open the real application");

        await using var db = _store.Create();
        (await db.Tenants.SingleAsync(t => t.Id == twin)).Fqdn.Should().EndWith(Tenant.TwinFqdnSuffix,
            "« ~ » cannot be a host: no lookup by address ever lands on the twin");
    }

    [Fact]
    public async Task A_twin_has_no_twin_and_an_unknown_tenant_none_at_all()
    {
        var twin = await EnsureAsync();

        await using var db = _store.Create();
        var handler = new EnsureTestTwinHandler(db, NullLogger<EnsureTestTwinHandler>.Instance);
        (await handler.Handle(new EnsureTestTwinCommand(twin), default)).Should().BeNull();
        (await handler.Handle(new EnsureTestTwinCommand(Guid.NewGuid()), default)).Should().BeNull();
    }

    [Fact]
    public async Task The_twin_answers_with_its_parents_modules_and_follows_its_parents_access()
    {
        await using (var db = _store.Create())
        {
            db.TenantModuleSubscriptions.Add(TenantModuleSubscription.ActivateDirect(
                _store.TenantId, TenantModules.Insurance, null, ModuleSubscriptionSource.Direct, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        var twin = await EnsureAsync();

        (await GetAsync(twin)).ActiveModules.Should().Equal(TenantModules.Insurance);
        (await GetAsync(twin)).IsActive.Should().BeTrue();

        await using (var db = _store.Create())
            await new SetTenantActivationHandler(db, NullLogger<SetTenantActivationHandler>.Instance)
                .Handle(new SetTenantActivationCommand(_store.TenantId, false), default);

        // An institution cut off is cut off in both modes.
        (await GetAsync(twin)).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task The_platform_list_shows_institutions_and_the_api_list_includes_twins()
    {
        var twin = await EnsureAsync();

        await using var db = _store.Create();
        var handler = new ListTenantsHandler(db);

        (await handler.Handle(new ListTenantsQuery(null), default)).Select(t => t.Id).Should().Equal(_store.TenantId);
        (await handler.Handle(new ListTenantsQuery(true, IncludeTwins: true), default)).Select(t => t.Id)
            .Should().BeEquivalentTo([_store.TenantId, twin]);
        (await new GetTenantByFqdnHandler(db).Handle(new GetTenantByFqdnQuery("https://crm.caisse.ci"), default))!.Id
            .Should().Be(_store.TenantId);
    }
}

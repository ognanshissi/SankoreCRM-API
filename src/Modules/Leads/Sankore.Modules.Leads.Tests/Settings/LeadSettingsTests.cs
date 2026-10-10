// Deliberately NOT Sankore.Modules.Leads.Tests.Infrastructure: that namespace shadows
// Sankore.Modules.Leads.Infrastructure inside this assembly, and existing tests that write
// `Infrastructure.LeadsDbContext` stop compiling the moment a file claims it.
namespace Sankore.Modules.Leads.Tests.Settings;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Covers <see cref="LeadSettingsService"/> and <see cref="LeadSettingSeeder"/> — the per-tenant
/// M13 parameter store introduced for BNF-B-09's health-response retention.
/// </summary>
public sealed class LeadSettingsTests
{
    // A FRESH tenant id per test, and not a static one. LeadSettingsService caches in a STATIC
    // ConcurrentDictionary keyed by (tenantId, key) with a 60-second TTL — process-local by design,
    // and correct in production — so two tests sharing a tenant id see each other's writes
    // whatever order they run in. xUnit builds one instance per test, so instance fields give each
    // test its own tenant. CustomerSettingsService has the identical static cache; the same rule
    // applies to any test written against it.
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();

    private sealed class StubTenantStore(params Guid[] tenantIds) : ITenantStore
    {
        public Task<TenantInfo?> GetAsync(Guid tenantId, CancellationToken ct = default)
            => Task.FromResult<TenantInfo?>(null);

        public Task<TenantInfo?> GetByFqdnAsync(string fqdn, CancellationToken ct = default)
            => Task.FromResult<TenantInfo?>(null);

        public Task<IReadOnlyList<TenantInfo>> GetAllActiveAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TenantInfo>>(
                [.. tenantIds.Select(id => new TenantInfo(
                    id, $"tenant-{id}", $"{id}.sankore.test", true, false, null, null))]);
    }

    // ── Reads ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Falls_back_to_the_declared_default_when_the_tenant_has_no_row()
    {
        // A freshly created tenant must behave correctly before the seeder has run — otherwise the
        // first boot after a tenant is provisioned is a window with no retention policy at all.
        using var factory = new TestDbContextFactory(_tenant);
        var settings = new LeadSettingsService(factory.CreateContext());

        var days = await settings.GetIntAsync(
            _tenant, LeadSettingKeys.HealthResponseRetentionDays, CancellationToken.None);

        days.Should().Be(90);
    }

    [Fact]
    public async Task Reads_the_tenants_own_value_once_it_is_set()
    {
        using var factory = new TestDbContextFactory(_tenant);

        var writer = new LeadSettingsService(factory.CreateContext());
        var write = await writer.SetAsync(
            _tenant, LeadSettingKeys.HealthResponseRetentionDays, "30", Actor, CancellationToken.None);
        write.IsSuccess.Should().BeTrue();

        var reader = new LeadSettingsService(factory.CreateContext());
        var days = await reader.GetIntAsync(
            _tenant, LeadSettingKeys.HealthResponseRetentionDays, CancellationToken.None);

        days.Should().Be(30);
    }

    [Fact]
    public async Task A_value_that_no_longer_parses_falls_back_instead_of_throwing()
    {
        // A hand-edited row or a changed value type must not take the purge job down with it.
        using var factory = new TestDbContextFactory(_tenant);

        await using (var seed = factory.CreateContext())
        {
            seed.LeadSettings.Add(LeadSetting.Create(
                _tenant, LeadSettingKeys.HealthResponseRetentionDays, "thirty",
                LeadSettingKeys.TypeInt, null));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var settings = new LeadSettingsService(factory.CreateContext());
        var days = await settings.GetIntAsync(
            _tenant, LeadSettingKeys.HealthResponseRetentionDays, CancellationToken.None);

        days.Should().Be(90, "the declared default stands in for an unparseable row");
    }

    [Fact]
    public async Task GetAll_returns_every_declared_key_even_before_seeding()
    {
        using var factory = new TestDbContextFactory(_tenant);
        var settings = new LeadSettingsService(factory.CreateContext());

        var all = await settings.GetAllAsync(_tenant, CancellationToken.None);

        all.Keys.Should().BeEquivalentTo(LeadSettingKeys.Defaults.Select(d => d.Key));
    }

    // ── Writes ────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_undeclared_key_is_refused()
    {
        // Settings are a closed list, not a free-form key/value store: an unknown key is a caller
        // bug, and silently creating the row would hide a typo forever.
        using var factory = new TestDbContextFactory(_tenant);
        var settings = new LeadSettingsService(factory.CreateContext());

        var result = await settings.SetAsync(
            _tenant, "health-retention-days", "30", Actor, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("SETTING_UNKNOWN");
    }

    [Fact]
    public async Task A_first_write_materialises_the_row_without_the_seeder()
    {
        using var factory = new TestDbContextFactory(_tenant);
        var settings = new LeadSettingsService(factory.CreateContext());

        await settings.SetAsync(
            _tenant, LeadSettingKeys.HealthResponseRetentionDays, "45", Actor, CancellationToken.None);

        await using var assertCtx = factory.CreateContext();
        var row = await assertCtx.LeadSettings.IgnoreQueryFilters()
            .SingleAsync(s => s.TenantId == _tenant, CancellationToken.None);

        row.Value.Should().Be("45");
        row.UpdatedBy.Should().Be(Actor);
        row.ValueType.Should().Be(LeadSettingKeys.TypeInt, "taken from the declaration, not the caller");
    }

    [Fact]
    public async Task One_tenants_value_is_invisible_to_another()
    {
        using var factory = new TestDbContextFactory(_tenant);

        var writer = new LeadSettingsService(factory.CreateContext());
        await writer.SetAsync(
            _tenant, LeadSettingKeys.HealthResponseRetentionDays, "7", Actor, CancellationToken.None);

        // Every read goes through IgnoreQueryFilters plus an explicit predicate, because the purge
        // job has no ambient tenant. That is precisely the shape in which a missing predicate
        // leaks, so it is asserted rather than assumed.
        var reader = new LeadSettingsService(factory.CreateContext());
        var otherDays = await reader.GetIntAsync(
            _otherTenant, LeadSettingKeys.HealthResponseRetentionDays, CancellationToken.None);

        otherDays.Should().Be(90);
    }

    // ── Seeder ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_seeder_creates_one_row_per_declared_key_per_tenant()
    {
        using var factory = new TestDbContextFactory(_tenant);

        await using var db = factory.CreateContext();
        await LeadSettingSeeder.SeedAsync(
            db, new StubTenantStore(_tenant, _otherTenant), NullLogger.Instance, CancellationToken.None);

        await using var assertCtx = factory.CreateContext();
        var rows = await assertCtx.LeadSettings.IgnoreQueryFilters().ToListAsync(CancellationToken.None);

        rows.Should().HaveCount(LeadSettingKeys.Defaults.Count * 2);
        rows.Where(r => r.TenantId == _tenant).Should().HaveCount(LeadSettingKeys.Defaults.Count);
        rows.Where(r => r.TenantId == _otherTenant).Should().HaveCount(LeadSettingKeys.Defaults.Count);
    }

    [Fact]
    public async Task The_seeder_never_overwrites_an_operators_change()
    {
        // It runs on every boot. A seeder that reset a retention period to 90 on each restart
        // would quietly undo a compliance decision.
        using var factory = new TestDbContextFactory(_tenant);

        var settings = new LeadSettingsService(factory.CreateContext());
        await settings.SetAsync(
            _tenant, LeadSettingKeys.HealthResponseRetentionDays, "30", Actor, CancellationToken.None);

        await using var db = factory.CreateContext();
        await LeadSettingSeeder.SeedAsync(
            db, new StubTenantStore(_tenant), NullLogger.Instance, CancellationToken.None);

        await using var assertCtx = factory.CreateContext();
        var rows = await assertCtx.LeadSettings.IgnoreQueryFilters()
            .Where(s => s.TenantId == _tenant).ToListAsync(CancellationToken.None);

        rows.Should().HaveCount(LeadSettingKeys.Defaults.Count, "no duplicate row was inserted");
        rows.Single(r => r.Key == LeadSettingKeys.HealthResponseRetentionDays)
            .Value.Should().Be("30");
    }

    [Fact]
    public async Task The_seeder_skips_the_system_placeholder_tenant()
    {
        using var factory = new TestDbContextFactory(_tenant);

        await using var db = factory.CreateContext();
        await LeadSettingSeeder.SeedAsync(
            db, new StubTenantStore(Guid.Empty), NullLogger.Instance, CancellationToken.None);

        await using var assertCtx = factory.CreateContext();
        (await assertCtx.LeadSettings.IgnoreQueryFilters().CountAsync(CancellationToken.None))
            .Should().Be(0);
    }

    [Fact]
    public void Every_declared_default_parses_as_its_own_value_type()
    {
        // The service falls back to Default(key) when a stored value does not parse. If a DECLARED
        // default did not parse either, that fallback would throw — on the purge job, in
        // production, for a key nobody had touched.
        foreach (var declared in LeadSettingKeys.Defaults)
        {
            var parsed = declared.ValueType switch
            {
                LeadSettingKeys.TypeInt     => int.TryParse(declared.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _),
                LeadSettingKeys.TypeBool    => bool.TryParse(declared.Value, out _),
                LeadSettingKeys.TypeDecimal => decimal.TryParse(declared.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _),
                _                           => true,
            };

            parsed.Should().BeTrue($"the default for '{declared.Key}' must parse as {declared.ValueType}");
        }
    }
}

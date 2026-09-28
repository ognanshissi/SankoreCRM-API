namespace Sankore.Modules.Customers.Tests.Features.Compliance;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// US-M01-BE-01 acceptance criteria on tenant bootstrapping.
/// <para>
/// The seeder runs on EVERY boot, so "it fills a new tenant" is only half the requirement — the
/// half that matters in production is that it does nothing on the boots that follow. A seeder
/// that re-inserted would break the unique index; one that overwrote would silently reset a
/// threshold an operator had deliberately changed, and nobody would notice until a client was
/// refused for being under age.
/// </para>
/// </summary>
public sealed class CustomerSeederTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly ITenantStore _tenantStore = Substitute.For<ITenantStore>();

    public CustomerSeederTests()
    {
        _factory = new TestCustomersDbContextFactory(_tenantId);

        _tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TenantInfo>>([TenantOf(_tenantId)]));
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_new_tenant_receives_every_declared_setting_with_its_factory_default()
    {
        await using var db = _factory.CreateContext();

        await CustomerSeeder.SeedAsync(db, _tenantStore, NullLogger.Instance, CancellationToken.None);

        var seeded = await db.CustomerSettings
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == _tenantId)
            .ToListAsync();

        seeded.Select(s => s.Key)
            .Should().BeEquivalentTo(CustomerSettingKeys.Defaults.Select(d => d.Key));

        foreach (var declared in CustomerSettingKeys.Defaults)
        {
            seeded.Single(s => s.Key == declared.Key).Value
                .Should().Be(declared.Value, $"'{declared.Key}' must start at its factory default");
        }
    }

    [Fact]
    public async Task A_new_tenant_receives_every_default_legal_form_active()
    {
        await using var db = _factory.CreateContext();

        await CustomerSeeder.SeedAsync(db, _tenantStore, NullLogger.Instance, CancellationToken.None);

        var forms = await db.LegalForms
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == _tenantId)
            .ToListAsync();

        forms.Should().NotBeEmpty();
        forms.Should().OnlyContain(f => f.IsActive);

        // The catalogue must contain the forms the legal-entity slice validates against; SARL and
        // SA are the two a UEMOA deployment cannot open without.
        forms.Select(f => f.Code).Should().Contain(["SARL", "SA"]);
    }

    [Fact]
    public async Task Replaying_the_seed_inserts_nothing_the_second_time()
    {
        await using var db = _factory.CreateContext();

        await CustomerSeeder.SeedAsync(db, _tenantStore, NullLogger.Instance, CancellationToken.None);

        var settingsAfterFirst = await CountSettingsAsync(db);
        var formsAfterFirst = await CountLegalFormsAsync(db);

        settingsAfterFirst.Should().Be(CustomerSettingKeys.Defaults.Count);

        // Two more boots, exactly as a rolling deploy would do.
        await CustomerSeeder.SeedAsync(db, _tenantStore, NullLogger.Instance, CancellationToken.None);
        await CustomerSeeder.SeedAsync(db, _tenantStore, NullLogger.Instance, CancellationToken.None);

        (await CountSettingsAsync(db)).Should().Be(settingsAfterFirst);
        (await CountLegalFormsAsync(db)).Should().Be(formsAfterFirst);
    }

    [Fact]
    public async Task A_value_the_tenant_has_changed_survives_a_re_seed()
    {
        await using var db = _factory.CreateContext();

        await CustomerSeeder.SeedAsync(db, _tenantStore, NullLogger.Instance, CancellationToken.None);

        // The operator raises the minimum age and lengthens the retention period.
        var minimumAge = await db.CustomerSettings
            .AsTracking()
            .IgnoreQueryFilters()
            .SingleAsync(s => s.TenantId == _tenantId && s.Key == CustomerSettingKeys.MinimumAge);

        var retention = await db.CustomerSettings
            .AsTracking()
            .IgnoreQueryFilters()
            .SingleAsync(s => s.TenantId == _tenantId && s.Key == CustomerSettingKeys.RetentionYears);

        var actor = Guid.NewGuid();
        minimumAge.SetValue("21", actor);
        retention.SetValue("15", actor);
        await db.SaveChangesAsync();

        await CustomerSeeder.SeedAsync(db, _tenantStore, NullLogger.Instance, CancellationToken.None);

        var reread = await db.CustomerSettings
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == _tenantId)
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        reread[CustomerSettingKeys.MinimumAge].Should().Be("21");
        reread[CustomerSettingKeys.RetentionYears].Should().Be("15");
    }

    [Fact]
    public async Task A_setting_deleted_by_hand_is_restored_on_the_next_boot()
    {
        await using var db = _factory.CreateContext();

        await CustomerSeeder.SeedAsync(db, _tenantStore, NullLogger.Instance, CancellationToken.None);

        var threshold = await db.CustomerSettings
            .AsTracking()
            .IgnoreQueryFilters()
            .SingleAsync(s => s.TenantId == _tenantId
                              && s.Key == CustomerSettingKeys.DuplicateScoreThreshold);

        db.CustomerSettings.Remove(threshold);
        await db.SaveChangesAsync();

        await CustomerSeeder.SeedAsync(db, _tenantStore, NullLogger.Instance, CancellationToken.None);

        (await CountSettingsAsync(db)).Should().Be(CustomerSettingKeys.Defaults.Count);
    }

    [Fact]
    public async Task Seeding_one_tenant_leaves_another_tenants_rows_untouched()
    {
        var otherTenantId = Guid.NewGuid();

        _tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TenantInfo>>(
                [TenantOf(_tenantId), TenantOf(otherTenantId)]));

        await using var db = _factory.CreateContext();

        await CustomerSeeder.SeedAsync(db, _tenantStore, NullLogger.Instance, CancellationToken.None);

        // Each tenant gets its own complete, independent set of rows.
        var perTenant = await db.CustomerSettings
            .IgnoreQueryFilters()
            .GroupBy(s => s.TenantId)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToListAsync();

        perTenant.Should().HaveCount(2);
        perTenant.Should().OnlyContain(x => x.Count == CustomerSettingKeys.Defaults.Count);
    }

    [Fact]
    public async Task The_SYSTEM_placeholder_tenant_is_never_seeded()
    {
        _tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TenantInfo>>(
                [TenantOf(Guid.Empty), TenantOf(_tenantId)]));

        await using var db = _factory.CreateContext();

        await CustomerSeeder.SeedAsync(db, _tenantStore, NullLogger.Instance, CancellationToken.None);

        var rowsForPlaceholder = await db.CustomerSettings
            .IgnoreQueryFilters()
            .CountAsync(s => s.TenantId == Guid.Empty);

        rowsForPlaceholder.Should().Be(0);
    }

    private Task<int> CountSettingsAsync(CustomersDbContext db) => db.CustomerSettings
        .IgnoreQueryFilters()
        .CountAsync(s => s.TenantId == _tenantId);

    private Task<int> CountLegalFormsAsync(CustomersDbContext db) => db.LegalForms
        .IgnoreQueryFilters()
        .CountAsync(f => f.TenantId == _tenantId);

    /// <summary>An active tenant record — only the id matters to the seeder.</summary>
    private static TenantInfo TenantOf(Guid id) => new(
        Id: id,
        Name: $"tenant-{id:N}"[..16],
        Fqdn: $"{id:N}.sankore.test",
        IsActive: true,
        IsMaintenance: false,
        TrialExpiresAt: null,
        BlockedAt: null);
}

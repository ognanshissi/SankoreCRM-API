namespace Sankore.Modules.Administration.Tests.Infrastructure;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Tests.TestSupport;
using Xunit;

/// <summary>
/// Pins <c>AdministrationModuleFacade.PlatformDefaultCompliance</c> to
/// <see cref="ComplianceSettings.CreateDefault"/>.
///
/// <para>
/// The facade answers a tenant with no row from a hard-coded record rather than by building the
/// entity, so that a READ never writes — three modules call <c>GetComplianceSettingsAsync</c> on
/// hot paths. The cost is two declarations of one set of defaults, and the failure mode is the
/// nasty kind: a tenant that has never opened the compliance screen would behave differently from
/// one that opened it and saved without changing anything. This test is what makes that
/// impossible to introduce silently.
/// </para>
/// </summary>
public sealed class ComplianceSettingsDefaultsTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    [Fact]
    public async Task The_facade_answers_a_tenant_with_no_row_with_the_entitys_own_defaults()
    {
        using var factory = new TestAdminDbContextFactory(Tenant);
        var facade = new AdministrationModuleFacade(factory.CreateContext(), new ProductCatalogCache());

        var fromFacade = await facade.GetComplianceSettingsAsync(Tenant, CancellationToken.None);

        var fromEntity = ComplianceSettings.CreateDefault(
            Tenant, Guid.NewGuid(), DateTimeOffset.UtcNow);

        fromFacade.FatcaEnabled.Should().Be(fromEntity.FatcaEnabled);
        fromFacade.FatcaRegime.Should().Be(fromEntity.FatcaRegime?.ToString());
        fromFacade.FatcaGiin.Should().Be(fromEntity.FatcaGiin);
        fromFacade.FatcaRequiredAt.Should().Be(fromEntity.FatcaRequiredAt.ToString());
        fromFacade.FatcaForCoHolders.Should().Be(fromEntity.FatcaForCoHolders);
        fromFacade.PepPostMandateMonths.Should().Be(fromEntity.PepPostMandateMonths);
    }

    [Fact]
    public async Task The_facade_never_returns_null()
    {
        // Returning null would hand every caller the same decision — "no row, so is FATCA on?" —
        // and one of them would eventually answer it the wrong way.
        using var factory = new TestAdminDbContextFactory(Tenant);
        var facade = new AdministrationModuleFacade(factory.CreateContext(), new ProductCatalogCache());

        var settings = await facade.GetComplianceSettingsAsync(
            Guid.NewGuid(), CancellationToken.None);

        settings.Should().NotBeNull();
        settings.FatcaEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task The_facade_reads_a_tenants_own_row_when_there_is_one()
    {
        using var factory = new TestAdminDbContextFactory(Tenant);

        await using (var seed = factory.CreateContext())
        {
            var settings = ComplianceSettings.CreateDefault(Tenant, Guid.NewGuid(), DateTimeOffset.UtcNow);
            settings.ConfigureFatca(
                enabled: true,
                regime: FatcaRegime.IgaModel1,
                giin: "98Q96B.00000.LE.384",
                requiredAt: FatcaRequiredAt.Enrichment,
                forCoHolders: false,
                actor: Guid.NewGuid(),
                now: DateTimeOffset.UtcNow);
            settings.SetPepPostMandateMonths(24, Guid.NewGuid(), DateTimeOffset.UtcNow);

            seed.ComplianceSettings.Add(settings);
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var facade = new AdministrationModuleFacade(factory.CreateContext(), new ProductCatalogCache());
        var read = await facade.GetComplianceSettingsAsync(Tenant, CancellationToken.None);

        read.FatcaEnabled.Should().BeTrue();
        read.FatcaRegime.Should().Be("IgaModel1");
        read.FatcaGiin.Should().Be("98Q96B.00000.LE.384");
        read.FatcaRequiredAt.Should().Be("Enrichment");
        read.FatcaForCoHolders.Should().BeFalse();
        read.PepPostMandateMonths.Should().Be(24);
    }

    [Fact]
    public async Task One_tenants_configuration_is_invisible_to_another()
    {
        // The facade reads with IgnoreQueryFilters and an explicit predicate, because its callers
        // are Hangfire jobs and consumers with no ambient tenant. That is exactly the shape in
        // which a missing predicate leaks — so it is asserted rather than assumed.
        var other = Guid.NewGuid();
        using var factory = new TestAdminDbContextFactory(Tenant);

        await using (var seed = factory.CreateContext())
        {
            var settings = ComplianceSettings.CreateDefault(Tenant, Guid.NewGuid(), DateTimeOffset.UtcNow);
            settings.ConfigureFatca(
                true, FatcaRegime.IgaModel1, "98Q96B.00000.LE.384",
                FatcaRequiredAt.Conversion, true, Guid.NewGuid(), DateTimeOffset.UtcNow);
            seed.ComplianceSettings.Add(settings);
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var facade = new AdministrationModuleFacade(factory.CreateContext(), new ProductCatalogCache());
        var read = await facade.GetComplianceSettingsAsync(other, CancellationToken.None);

        read.FatcaEnabled.Should().BeFalse("the other tenant has no row and runs on the defaults");
        read.FatcaGiin.Should().BeNull();
    }
}

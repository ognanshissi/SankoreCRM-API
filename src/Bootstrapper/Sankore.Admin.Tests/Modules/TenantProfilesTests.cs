namespace Sankore.Admin.Tests.Modules;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Features.Tenants.CreateTenant;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>The creation profiles of the product decision (§5.5), and what a request may combine.</summary>
public sealed class TenantProfilesTests : IDisposable
{
    private readonly ModuleTestDb _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public void The_four_profiles_are_the_ones_the_product_owner_decided()
    {
        TenantProfiles.Find(TenantProfiles.ImfOrBank)!.Modules.Should().Equal(TenantModules.BankingMicrofinance);
        TenantProfiles.Find(TenantProfiles.CaisseWithInsurance)!.Modules
            .Should().Equal(TenantModules.BankingMicrofinance, TenantModules.Insurance);
        TenantProfiles.Find(TenantProfiles.InsuranceDistributor)!.Modules.Should().Equal(TenantModules.Insurance);
        TenantProfiles.Find(TenantProfiles.CrmOnly)!.Modules.Should().BeEmpty();
        TenantProfiles.All.SelectMany(p => p.Modules).Should().OnlyContain(m => TenantModules.IsKnown(m));
    }

    [Fact]
    public void A_request_names_a_profile_or_a_module_list_never_both()
    {
        TenantProfiles.ResolveModules("caissewithinsurance", null).Modules
            .Should().Equal(TenantModules.BankingMicrofinance, TenantModules.Insurance);
        TenantProfiles.ResolveModules(null, [TenantModules.Insurance]).Modules.Should().Equal(TenantModules.Insurance);
        TenantProfiles.ResolveModules(TenantProfiles.CrmOnly, [TenantModules.Insurance]).Error
            .Should().Be("PROFILE_AND_MODULES");
        TenantProfiles.ResolveModules("Telephonie", null).Error.Should().Be("PROFILE_UNKNOWN");
    }

    [Fact]
    public async Task A_Caisse_avec_assurance_starts_with_both_modules_active()
    {
        await using var db = _store.Create();
        var modules = TenantProfiles.ResolveModules(TenantProfiles.CaisseWithInsurance, null).Modules;

        var created = await new CreateTenantHandler(db).Handle(new CreateTenantCommand(
            "Caisse Rurale", "root@rurale.ci", "https://crm.rurale.ci", null, modules), default);

        (await db.TenantModuleSubscriptions.Where(s => s.TenantId == created.Id).Select(s => s.ModuleCode).ToListAsync())
            .Should().BeEquivalentTo([TenantModules.BankingMicrofinance, TenantModules.Insurance]);
    }
}

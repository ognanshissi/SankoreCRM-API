namespace Sankore.Modules.Administration.Tests.Infrastructure;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Who a workflow « Notification » step addressed to a role reaches: the active holders of the
/// role in the record's agency or ABOVE it — a branch manager hears about their branch, a
/// head-office officer about every branch, a neighbouring branch about nothing.
/// </summary>
public sealed class ListActiveUsersInRolesTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public ListActiveUsersInRolesTests() => _factory = new TestAdminDbContextFactory(_tenantId);
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task The_role_is_looked_up_in_the_agency_and_its_parents_only()
    {
        var hq = Agency.Create(_tenantId, "HQ", "Siège", "", AgencyType.HeadQuarter, null, null);
        var branch = Agency.Create(_tenantId, "B1", "Plateau", "", AgencyType.Branch, hq.Id, null);
        var other = Agency.Create(_tenantId, "B2", "Yopougon", "", AgencyType.Branch, hq.Id, null);
        var manager = AppRole.Create("BranchManager", "Chef d'agence");
        var compliance = AppRole.Create("ComplianceOfficer", "Conformité");

        await using (var db = _factory.CreateContext())
        {
            db.Agencies.AddRange(hq, branch, other);
            foreach (var role in new[] { manager, compliance })
            {
                role.NormalizedName = role.Name!.ToUpperInvariant();
                db.Roles.Add(role);
            }
            await db.SaveChangesAsync();
        }

        var branchManager = await SeedUserAsync(branch.Id, manager);
        var otherManager = await SeedUserAsync(other.Id, manager);
        var officer = await SeedUserAsync(hq.Id, compliance);
        var inactive = await SeedUserAsync(branch.Id, manager, active: false);

        var inBranch = await Facade().ListActiveUsersInRolesAsync(
            _tenantId, ["branchmanager", "complianceofficer"], branch.Id, default);
        var tenantWide = await Facade().ListActiveUsersInRolesAsync(_tenantId, ["BranchManager"], null, default);

        inBranch.Should().BeEquivalentTo([branchManager, officer]);
        tenantWide.Should().BeEquivalentTo([branchManager, otherManager]);
        inBranch.Should().NotContain(inactive);
    }

    private async Task<Guid> SeedUserAsync(Guid agencyId, AppRole role, bool active = true)
    {
        await using var db = _factory.CreateContext();
        var user = AppUser.Create(_tenantId, agencyId, "Awa", "Ouattara", $"{Guid.NewGuid():N}@sankore.ci");
        if (active) user.Activate();
        db.Users.Add(user);
        db.UserRoles.Add(UserRole.Assign(_tenantId, user.Id, role.Id, Guid.NewGuid()));
        await db.SaveChangesAsync();
        return user.Id;
    }

    private AdministrationModuleFacade Facade() => new(
        _factory.CreateContext(),
        new ProductCatalogCache(),
        new ModuleEmailSender(Substitute.For<INotificationsModule>(), Substitute.For<ITenantStore>(),
            NullLogger<ModuleEmailSender>.Instance));
}

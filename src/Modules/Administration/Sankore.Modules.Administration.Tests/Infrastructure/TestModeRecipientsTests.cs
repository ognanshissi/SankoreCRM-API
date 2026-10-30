namespace Sankore.Modules.Administration.Tests.Infrastructure;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Test mode, sandbox lot 1: a tenant marked test writes only to its own team. Every sender of
/// the platform asks these two questions before a message leaves, so what is pinned here is the
/// whole allow-list — and that a live tenant is never touched by it.
/// </summary>
public sealed class TestModeRecipientsTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;
    private readonly ITenantStore _tenants = Substitute.For<ITenantStore>();

    public TestModeRecipientsTests()
    {
        _factory = new TestAdminDbContextFactory(_tenantId);
        MarkTest(true);
    }

    public void Dispose() => _factory.Dispose();

    private AdministrationModuleFacade Facade() => new(_factory.CreateContext(), new ProductCatalogCache(), tenants: _tenants);

    private void MarkTest(bool isTest)
        => _tenants.GetAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantInfo(_tenantId, "Caisse test", "test.sankore.local", true, false, null, null, IsTest: isTest));

    [Fact]
    public async Task A_live_tenant_blocks_nobody()
    {
        MarkTest(false);

        (await Facade().IsEmailBlockedInTestAsync(_tenantId, "client@gmail.com", default)).Should().BeFalse();
        (await Facade().IsPhoneBlockedInTestAsync(_tenantId, "+2250102030405", default)).Should().BeFalse();
    }

    [Fact]
    public async Task An_unknown_tenant_or_no_registry_reads_as_live()
    {
        var unknown = Guid.NewGuid();
        (await Facade().IsEmailBlockedInTestAsync(unknown, "client@gmail.com", default)).Should().BeFalse();

        var withoutRegistry = new AdministrationModuleFacade(_factory.CreateContext(), new ProductCatalogCache());
        (await withoutRegistry.IsEmailBlockedInTestAsync(_tenantId, "client@gmail.com", default)).Should().BeFalse();
    }

    [Fact]
    public async Task A_test_tenant_writes_to_its_users_sign_in_address_whatever_the_case()
    {
        Seed("Awa");

        (await Facade().IsEmailBlockedInTestAsync(_tenantId, " AWA@example.test ", default)).Should().BeFalse();
        (await Facade().IsEmailBlockedInTestAsync(_tenantId, "client@gmail.com", default)).Should().BeTrue();
    }

    [Fact]
    public async Task A_users_additional_address_counts_too()
    {
        var user = Seed("Awa");
        using (var db = _factory.CreateContext())
        {
            var profile = UserProfile.Create(_tenantId, user.Id);
            typeof(UserProfile).GetProperty(nameof(UserProfile.AdditionalEmail))!.SetValue(profile, "awa.kone@perso.test");
            db.UserProfiles.Add(profile);
            db.SaveChanges();
        }

        (await Facade().IsEmailBlockedInTestAsync(_tenantId, "Awa.Kone@perso.test", default)).Should().BeFalse();
    }

    [Fact]
    public async Task Another_tenants_user_is_not_part_of_the_team()
    {
        Seed("Etranger", Guid.NewGuid());

        (await Facade().IsEmailBlockedInTestAsync(_tenantId, "etranger@example.test", default)).Should().BeTrue();
    }

    [Fact]
    public async Task A_number_typed_locally_in_a_profile_matches_its_international_form()
    {
        var user = Seed("Awa");
        using (var db = _factory.CreateContext())
        {
            var profile = UserProfile.Create(_tenantId, user.Id);
            profile.UpdateBySelf("07 08 09 18 01", "+225 05 44 33 22 11", null, "fr", "Africa/Abidjan", AmountFormat.SpaceGrouped);
            db.UserProfiles.Add(profile);
            db.SaveChanges();
        }

        (await Facade().IsPhoneBlockedInTestAsync(_tenantId, "+2250708091801", default)).Should().BeFalse();
        (await Facade().IsPhoneBlockedInTestAsync(_tenantId, "002250544332211", default)).Should().BeFalse();
        (await Facade().IsPhoneBlockedInTestAsync(_tenantId, "+2250102030405", default)).Should().BeTrue();
    }

    [Fact]
    public async Task A_test_tenant_with_no_numbers_on_file_rings_nobody()
    {
        Seed("Awa");

        (await Facade().IsPhoneBlockedInTestAsync(_tenantId, "+2250708091801", default)).Should().BeTrue();
    }

    [Theory]
    [InlineData("0708091801", "+2250708091801", true)]
    [InlineData("+225 07 08 09 18 01", "002250708091801", true)]
    [InlineData("0708091801", "+2250708091802", false)]
    // Below eight digits an extension would match half the country.
    [InlineData("1801", "+2250708091801", false)]
    [InlineData(null, "+2250708091801", false)]
    public void Numbers_are_compared_on_their_last_digits(string? a, string b, bool same)
        => TestModePhones.Same(a, b).Should().Be(same);

    private AppUser Seed(string name, Guid? tenantId = null)
    {
        var user = AppUser.CreateRoot(tenantId ?? _tenantId, name, "Test", $"{name}@example.test");
        // What UserManager.CreateAsync writes; the factory alone leaves it empty.
        user.NormalizedEmail = user.Email!.ToUpperInvariant();
        using var db = _factory.CreateContext();
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }
}

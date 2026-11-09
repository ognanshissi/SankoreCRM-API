namespace Sankore.Modules.Administration.Tests.Features.StartingData;

using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Agencies.AssignAgencyManager;
using Sankore.Modules.Administration.Features.Agencies.CreateAgency;
using Sankore.Modules.Administration.Features.StartingData;
using Sankore.Modules.Administration.Features.Territories.CreateTerritory;
using Sankore.Modules.Administration.Features.Users.AssignManager;
using Sankore.Modules.Administration.Features.Users.DemoUsers;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The demo's accounts (docs/donnees-demarrage-demo.md, lot 2): one per profile with the shared
/// password, the branches, their managers and reporting lines, two territories — a demo load only.
/// </summary>
public sealed class AdministrationDemoDataTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _db;
    private readonly ISender _sender = Substitute.For<ISender>();
    private readonly List<object> _sent = [];

    public AdministrationDemoDataTests()
    {
        _db = new TestAdminDbContextFactory(_tenantId);
        using (var db = _db.CreateContext())
        {
            db.Agencies.Add(Agency.Create(_tenantId, "AG0001", "Siège", "", AgencyType.HeadQuarter, null, null));
            db.SaveChanges();
        }
        _sender.Send(Arg.Any<CreateAgencyCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _sent.Add(ci[0]); return Result.Ok(new CreateAgencyResult(Guid.NewGuid(), "AG")); });
        _sender.Send(Arg.Any<ProvisionDemoUserCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _sent.Add(ci[0]); return Result.Ok(Guid.NewGuid()); });
        _sender.Send(Arg.Any<CreateTerritoryCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _sent.Add(ci[0]); return Result.Ok(new CreateTerritoryResult(Guid.NewGuid())); });
        _sender.Send(Arg.Any<AssignManagerCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _sent.Add(ci[0]); return Result.Ok(new AssignManagerResult(Guid.Empty, Guid.Empty, "", null, true)); });
        _sender.Send(Arg.Any<AssignAgencyManagerCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _sent.Add(ci[0]); return Result.Ok(new AssignAgencyManagerResult(Guid.Empty, Guid.Empty, "", null, true, "BranchManager")); });
    }

    public void Dispose() => _db.Dispose();

    private Task<IReadOnlyDictionary<string, int>> LoadAsync(StartingDataKind kind, string password = "Demo-2026!", ITenantModules? modules = null)
    {
        var services = new ServiceCollection().AddSingleton(_db.CreateContext()).AddSingleton(_sender)
            .AddSingleton(modules ?? FixedTenantModules.All).BuildServiceProvider();
        return new AdministrationDemoData(Options.Create(new DemoDataOptions { Password = password }))
            .LoadAsync(new StartingDataRequest(_tenantId, "Démo", kind, Guid.NewGuid()), services, default);
    }

    [Fact]
    public async Task A_starter_load_creates_no_fictitious_account()
    {
        (await LoadAsync(StartingDataKind.Starter)).Should().BeEmpty();
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_the_configured_password_the_demo_says_what_is_missing()
        => await FluentActions.Awaiting(() => LoadAsync(StartingDataKind.Demo, password: " "))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*DemoData:Password*");

    [Fact]
    public async Task Every_profile_gets_an_account_at_its_sub_address_with_the_shared_password()
    {
        var created = await LoadAsync(StartingDataKind.Demo);

        created.Should().Equal(new Dictionary<string, int> { ["Agences"] = 2, ["Comptes de démo"] = 15, ["Territoires"] = 2 });
        var accounts = _sent.OfType<ProvisionDemoUserCommand>().ToList();
        accounts.Select(a => a.RoleCode).Distinct().Should().BeEquivalentTo(
            Roles.All.Where(r => r != Roles.System).Select(r => r.Code));
        accounts.Should().OnlyContain(a => a.Email.StartsWith("contact+") && a.Email.EndsWith("@talisoft.net") && a.Password == "Demo-2026!");
        accounts.Select(a => a.Email).Should().OnlyHaveUniqueItems();
        accounts.Single(a => a.LastName == "Touré").Email.Should().Be("contact+administrateur.demo@talisoft.net");
        // Everyone but the administrator reports to someone.
        _sent.OfType<AssignManagerCommand>().Should().HaveCount(14);
        _sent.OfType<AssignAgencyManagerCommand>().Should().HaveCount(2);
    }

    [Fact]
    public async Task A_socle_demo_has_no_credit_nor_cash_account()
    {
        // docs/isolation-socle.md, lot 2: those roles are Banking - Microfinance's.
        var created = await LoadAsync(StartingDataKind.Demo, modules: FixedTenantModules.None);

        created["Comptes de démo"].Should().Be(11);
        _sent.OfType<ProvisionDemoUserCommand>().Select(a => a.RoleCode).Should()
            .NotContain([Roles.CreditAnalyst.Code, Roles.CreditManager.Code, Roles.Cashier.Code, Roles.AccountManager.Code]);
        _sent.OfType<AssignManagerCommand>().Should().HaveCount(10);
    }

    [Theory]
    [InlineData("Démo Caisse d'Abidjan", "demo-caisse-d-abidjan")]
    [InlineData("  Recette démo assurance  ", "recette-demo-assurance")]
    [InlineData("Institution de microfinance de la région du Gôh-Djiboua", "institution-de-microfinance-de")]
    [InlineData("***", "demo")]
    public void The_institution_part_is_a_short_ascii_slug(string name, string slug)
        => DemoDataOptions.InstitutionSlug(name).Should().Be(slug);
}

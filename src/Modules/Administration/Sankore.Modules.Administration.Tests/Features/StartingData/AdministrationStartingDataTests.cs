namespace Sankore.Modules.Administration.Tests.Features.StartingData;

using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Agencies.CreateAgency;
using Sankore.Modules.Administration.Features.Products.CreateProduct;
using Sankore.Modules.Administration.Features.StartingData;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The « Démarrage » of Administration (docs/donnees-demarrage-demo.md, 2026-11-08): « Siège » and
/// « Agence principale » for an institution without agencies, the catalogue for its paid modules only,
/// nothing twice.
/// </summary>
public sealed class AdministrationStartingDataTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _db;
    private readonly ISender _sender = Substitute.For<ISender>();
    private readonly ITenantModules _modules = Substitute.For<ITenantModules>();
    private readonly List<object> _sent = [];

    public AdministrationStartingDataTests()
    {
        _db = new TestAdminDbContextFactory(_tenantId);
        _sender.Send(Arg.Any<CreateAgencyCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _sent.Add(ci[0]); return Result.Ok(new CreateAgencyResult(Guid.NewGuid(), "AG")); });
        _sender.Send(Arg.Any<CreateProductCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _sent.Add(ci[0]); return Result.Ok(Guid.NewGuid()); });
    }

    public void Dispose() => _db.Dispose();

    private void Modules(params string[] enabled)
        => _modules.IsEnabledAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => enabled.Contains((string)ci[0]));

    private Task<IReadOnlyDictionary<string, int>> LoadAsync()
    {
        var services = new ServiceCollection()
            .AddSingleton(_db.CreateContext())
            .AddSingleton(_sender)
            .AddSingleton(_modules)
            .BuildServiceProvider();
        return new AdministrationStartingData().LoadAsync(
            new StartingDataRequest(_tenantId, "Caisse Avenir", StartingDataKind.Starter, Guid.NewGuid()), services, default);
    }

    [Fact]
    public async Task An_institution_with_both_modules_gets_two_agencies_and_the_whole_catalogue()
    {
        Modules(TenantModules.BankingMicrofinance, TenantModules.Insurance);

        var created = await LoadAsync();

        created.Should().Equal(new Dictionary<string, int> { ["Agences"] = 2, ["Produits"] = 9 });
        var agencies = _sent.OfType<CreateAgencyCommand>().ToList();
        agencies.Select(a => a.Name).Should().Equal(AdministrationStartingData.HeadQuarter, AdministrationStartingData.MainBranch);
        agencies[0].AgencyType.Should().Be(AgencyType.HeadQuarter);
        agencies[1].ParentAgencyId.Should().NotBeNull();
    }

    [Fact]
    public async Task A_socle_institution_gets_standard_products_and_nothing_financial()
    {
        // docs/isolation-socle.md, lot 5: no module, a catalogue of Standard offers to rename.
        Modules();

        var created = await LoadAsync();

        created["Produits"].Should().Be(4);
        _sent.OfType<CreateProductCommand>().Should().OnlyContain(p => p.Category == ProductCategory.Standard);
        _sent.OfType<CreateProductCommand>().Select(p => p.Code).Should().Contain("OFFRE-ESSENTIEL");
    }

    [Fact]
    public async Task Only_the_families_of_the_paid_modules_are_created()
    {
        Modules(TenantModules.Insurance);

        await LoadAsync();

        _sent.OfType<CreateProductCommand>().Select(p => p.Code)
            .Should().BeEquivalentTo("ASS-SANTE", "ASS-PREVOYANCE", "ASS-EMPRUNTEUR");
    }

    [Fact]
    public async Task An_institution_that_has_its_own_network_and_products_receives_nothing_twice()
    {
        Modules(TenantModules.BankingMicrofinance);
        await using (var db = _db.CreateContext())
        {
            db.Agencies.Add(Agency.Create(_tenantId, "AG0001", "Agence Dakar", "", AgencyType.HeadQuarter, null, null));
            db.ProductSpecialities.Add(ProductSpeciality.Create(_tenantId, "Mon épargne", "cpt-epargne", ProductCategory.Savings));
            await db.SaveChangesAsync();
        }

        var created = await LoadAsync();

        _sent.OfType<CreateAgencyCommand>().Should().BeEmpty();
        _sent.OfType<CreateProductCommand>().Select(p => p.Code).Should().NotContain("CPT-EPARGNE");
        created["Produits"].Should().Be(5);
    }
}

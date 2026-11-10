namespace Sankore.Modules.Leads.Tests.Features.StartingData;

using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Modules.Leads.Features.DispatchingRules.CreateDispatchingRule;
using Sankore.Modules.Leads.Features.LeadSources.CreateLeadSource;
using Sankore.Modules.Leads.Features.Opportunities.CreateOpportunity;
using Sankore.Modules.Leads.Features.Settings.KeyAccounts;
using Sankore.Modules.Leads.Features.StartingData;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The key-account prospecting of a socle demo (docs/prospection-grands-comptes.md, lot 5): thresholds,
/// a quote form, a key-account rule for the sales agents and five company prospects of every size —
/// and nothing of it for a module's demo, whose products are a credit or a policy.
/// </summary>
public sealed class LeadsCompanyDemoDataTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _db;
    private readonly ISender _sender = Substitute.For<ISender>();
    private readonly ITenantModules _modules = Substitute.For<ITenantModules>();
    private readonly List<object> _sent = [];

    public LeadsCompanyDemoDataTests()
    {
        _db = new TestDbContextFactory(_tenantId);
        _sender.Send(Arg.Any<IRequest<Result<Guid>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _sent.Add(ci[0]); return Result.Ok(Guid.NewGuid()); });
        _sender.Send(Arg.Any<IRequest<Result<CaptureLeadResult>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _sent.Add(ci[0]); return Result.Ok(new CaptureLeadResult(Guid.NewGuid(), "New")); });
        _sender.Send(Arg.Any<IRequest<Result<KeyAccountSettings>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _sent.Add(ci[0]); return Result.Ok(KeyAccountSettings.None); });
    }

    public void Dispose() => _db.Dispose();

    private Task<IReadOnlyDictionary<string, int>> LoadAsync(StartingDataKind kind = StartingDataKind.Demo, params string[] enabled)
    {
        _modules.IsEnabledAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => enabled.Contains((string)ci[1]));
        var services = new ServiceCollection()
            .AddSingleton(_db.CreateContext())
            .AddSingleton(_sender)
            .AddSingleton(_modules)
            .BuildServiceProvider();
        var request = new StartingDataRequest(_tenantId, "Recette Socle Démo", kind, Guid.NewGuid());
        foreach (var profile in new[] { "agent-commercial-plateau", "agent-commercial-yopougon", "agent-plateau-1" })
            request.Shared[DemoKeys.Account(profile)] = Guid.NewGuid();
        foreach (var agency in new[] { "Agence Plateau", "Agence Yopougon" })
            request.Shared[DemoKeys.Agency(agency)] = Guid.NewGuid();
        return new LeadsCompanyDemoData().LoadAsync(request, services, default);
    }

    [Fact]
    public async Task A_socle_demo_gets_thresholds_a_quote_form_a_key_account_rule_and_five_companies()
    {
        var created = await LoadAsync();

        created.Should().Equal(new Dictionary<string, int>
        {
            ["Réglage grands comptes"] = 1, ["Sources de lead"] = 1, ["Règles de dispatching"] = 1,
            ["Prospects entreprise"] = 5, ["Projets"] = 1,
        });

        var settings = _sent.OfType<SetKeyAccountSettingsCommand>().Single();
        settings.MajorPurchases.Should().Be(LeadsCompanyDemoData.MajorPurchases);

        var form = _sent.OfType<CreateLeadSourceCommand>().Single();
        form.ChannelType.Should().Be(LeadChannelType.WebForm, "a form: a product asked through it makes the lead hot");
        ((EmbeddedScriptSettings)form.Settings!).DefaultProductCode.Should().Be(LeadsCompanyDemoData.QuoteFormProduct);

        var rule = _sent.OfType<CreateDispatchingRuleCommand>().Single();
        rule.KeyAccountsOnly.Should().BeTrue();
        rule.ReservedAgentIds.Should().HaveCount(LeadsCompanyDemoData.KeyAccountAgents.Length);

        var prospects = _sent.OfType<CaptureLeadCommand>().ToList();
        prospects.Should().OnlyContain(p => p.ProspectType == LeadType.Corporate && p.CompanyName != null && p.OwnerId != null);
        _sent.OfType<CreateOpportunityCommand>().Should().ContainSingle();
    }

    [Fact]
    public async Task A_module_demo_and_a_starter_load_get_nothing()
    {
        (await LoadAsync(StartingDataKind.Demo, TenantModules.BankingMicrofinance)).Should().BeEmpty();
        (await LoadAsync(StartingDataKind.Starter)).Should().BeEmpty();
        _sent.Should().BeEmpty();
    }

    [Fact]
    public void The_companies_cover_every_tier_and_one_key_account_asks_through_the_form()
    {
        var thresholds = new KeyAccountSettings(LeadsCompanyDemoData.GoodPurchases, LeadsCompanyDemoData.MajorPurchases,
            LeadsCompanyDemoData.TargetSectors);
        var tiers = LeadsCompanyDemoData.Companies.Select(c => thresholds.TierOf(LeadType.Corporate, c.Purchases)).ToList();

        tiers.Distinct().Should().BeEquivalentTo(Enum.GetValues<LeadPotentialTier>());
        LeadsCompanyDemoData.Companies.Should().Contain(c =>
            c.SourceCode == LeadsCompanyDemoData.QuoteFormCode
            && thresholds.TierOf(LeadType.Corporate, c.Purchases) == LeadPotentialTier.KeyAccount);
        LeadsCompanyDemoData.Companies.Should().OnlyContain(c => LeadActivitySectors.Normalize(c.Sector) == c.Sector);
        LeadsCompanyDemoData.TargetSectors.Should().OnlyContain(s => LeadActivitySectors.Normalize(s) == s);
    }

    [Fact]
    public void Company_contacts_ask_for_socle_offers_and_have_their_own_numbers()
    {
        var socle = LeadsDemoData.ProductsFor(banking: false, insurance: false);
        LeadsCompanyDemoData.Companies.Should().OnlyContain(c => socle.Contains(c.Product));

        var phones = Enumerable.Range(1, LeadsCompanyDemoData.Companies.Count).Select(LeadsCompanyDemoData.Phone).ToList();
        var prospects = Enumerable.Range(1, LeadsDemoData.LeadCount).Select(LeadsDemoData.Phone);
        phones.Should().OnlyHaveUniqueItems().And.NotIntersectWith(prospects);
    }
}

namespace Sankore.Modules.Leads.Tests.Features.StartingData;

using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.ActivateLeadSource;
using Sankore.Modules.Leads.Features.LeadSources.CreateLeadSource;
using Sankore.Modules.Leads.Features.LeadSources.StartTestingLeadSource;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Features.QualificationTemplates.CreateQualificationTemplate;
using Sankore.Modules.Leads.Features.QualificationTemplates.PublishQualificationTemplate;
using Sankore.Modules.Leads.Features.StartingData;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The « Démarrage » of Leads (docs/donnees-demarrage-demo.md, 2026-11-08): internal sources active,
/// the web form in draft, the credit file for the Loan family, a questionnaire per paid family.
/// </summary>
public sealed class LeadsStartingDataTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _db;
    private readonly ISender _sender = Substitute.For<ISender>();
    private readonly ITenantModules _modules = Substitute.For<ITenantModules>();
    private readonly List<object> _sent = [];

    public LeadsStartingDataTests()
    {
        _db = new TestDbContextFactory(_tenantId);
        _sender.Send(Arg.Any<IRequest<Result<Guid>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _sent.Add(ci[0]); return Result.Ok(Guid.NewGuid()); });
        _sender.Send(Arg.Any<IRequest<Result>>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _sent.Add(ci[0]); return Result.Ok(); });
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
        return new LeadsStartingData().LoadAsync(
            new StartingDataRequest(_tenantId, "Caisse Avenir", StartingDataKind.Starter, Guid.NewGuid()), services, default);
    }

    [Fact]
    public async Task A_banking_and_insurance_institution_gets_sources_the_credit_file_and_three_questionnaires()
    {
        Modules(TenantModules.BankingMicrofinance, TenantModules.Insurance);

        var created = await LoadAsync();

        created.Should().Equal(new Dictionary<string, int>
            { ["Sources de lead"] = 5, ["Gabarits de projet"] = 1, ["Questionnaires"] = 3 });

        var sources = _sent.OfType<CreateLeadSourceCommand>().ToList();
        sources.Single(s => s.Code == LeadsStartingData.WebFormCode).IntegrationMode.Should().Be(IntegrationMode.EmbeddedScript);
        sources.Where(s => s.Code != LeadsStartingData.WebFormCode)
            .Should().OnlyContain(s => s.IntegrationMode == IntegrationMode.Internal);
        // The four internal sources go live; the web form waits for the institution's site.
        _sent.OfType<StartTestingLeadSourceCommand>().Should().HaveCount(4);
        _sent.OfType<ActivateLeadSourceCommand>().Should().HaveCount(4);
        _sent.OfType<CreateCreditFileTemplateCommand>().Should().ContainSingle();
        _sent.OfType<PublishQualificationTemplateCommand>().Should().HaveCount(3);
    }

    [Fact]
    public async Task Without_the_banking_module_no_credit_file_and_only_the_insurance_questionnaire()
    {
        Modules(TenantModules.Insurance);

        await LoadAsync();

        _sent.OfType<CreateCreditFileTemplateCommand>().Should().BeEmpty();
        _sent.OfType<CreateQualificationTemplateCommand>().Should().ContainSingle()
            .Which.ProductCategory.Should().Be(ProductCategory.HealthInsurance);
    }

    [Fact]
    public async Task A_socle_institution_gets_one_standard_questionnaire_and_no_credit_file()
    {
        // docs/isolation-socle.md, lot 5.
        Modules();

        await LoadAsync();

        _sent.OfType<CreateCreditFileTemplateCommand>().Should().BeEmpty();
        _sent.OfType<CreateQualificationTemplateCommand>().Should().ContainSingle()
            .Which.ProductCategory.Should().Be(ProductCategory.Standard);
    }

    [Fact]
    public async Task What_the_institution_already_has_is_left_alone()
    {
        Modules(TenantModules.BankingMicrofinance);
        await using (var db = _db.CreateContext())
        {
            db.LeadSourceConfigs.Add(LeadSourceConfig.Create(_tenantId, "AGENCE", "Mon agence", LeadChannelType.WalkIn, 0));
            await db.SaveChangesAsync();
        }

        await LoadAsync();

        _sent.OfType<CreateLeadSourceCommand>().Select(s => s.Code).Should().NotContain("AGENCE").And.HaveCount(4);
    }
}

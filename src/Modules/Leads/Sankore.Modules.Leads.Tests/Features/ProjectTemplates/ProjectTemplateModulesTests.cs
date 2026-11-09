namespace Sankore.Modules.Leads.Tests.Features.ProjectTemplates;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Isolation du socle, lot 1 (docs/isolation-socle.md) : sans module, un gabarit ne peut porter ni un
/// montant de crédit ou de dépôt (Banking - Microfinance), ni une prime (Assurance), ni une famille de
/// produits d'un module — sans quoi un « dossier de crédit » se fabrique à la main. Comme pour les
/// produits, seul le passage VERS un module est refusé.
/// </summary>
public sealed class ProjectTemplateModulesTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-09T09:00:00Z");

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly TimeProvider _clock = new FixedClock(Now);

    public ProjectTemplateModulesTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private static ProjectStepInput Step(string label) => new(null, label, OpportunityStage.NeedsAnalysis, 30);

    private async Task<Result<Guid>> CreateAsync(ITenantModules modules, ProductCategory? category, ProjectAmountKind kind)
    {
        await using var db = _factory.CreateContext();
        return await new CreateProjectTemplateHandler(db, _administration, new FixedTenantContext(_tenantId), _clock, modules)
            .Handle(new CreateProjectTemplateCommand("Parcours", category, null, [Step("Étude")],
                new ProjectForecastOptions(AmountKind: kind)), CancellationToken.None);
    }

    [Theory]
    [InlineData(ProjectAmountKind.Loan)]
    [InlineData(ProjectAmountKind.Deposit)]
    [InlineData(ProjectAmountKind.Premium)]
    public async Task The_socle_cannot_give_a_template_a_finance_amount(ProjectAmountKind kind)
        => (await CreateAsync(FixedTenantModules.None, null, kind)).Error.Should().Be(TenantModuleErrors.Disabled);

    [Fact]
    public async Task The_socle_keeps_the_generic_amount_and_the_standard_family()
    {
        (await CreateAsync(FixedTenantModules.None, null, ProjectAmountKind.Generic)).IsSuccess.Should().BeTrue();
        (await CreateAsync(FixedTenantModules.None, ProductCategory.Standard, ProjectAmountKind.Generic)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_family_of_a_module_needs_that_module()
        => (await CreateAsync(FixedTenantModules.None, ProductCategory.Loan, ProjectAmountKind.Generic))
            .Error.Should().Be(TenantModuleErrors.CategoryDisabled);

    [Fact]
    public async Task Each_amount_belongs_to_its_own_module()
    {
        var banking = new FixedTenantModules(TenantModules.BankingMicrofinance);
        (await CreateAsync(banking, ProductCategory.Loan, ProjectAmountKind.Loan)).IsSuccess.Should().BeTrue();
        (await CreateAsync(banking, null, ProjectAmountKind.Premium)).Error.Should().Be(TenantModuleErrors.Disabled);

        ProjectTemplateModules.ModuleOf(ProjectAmountKind.Deposit).Should().Be(TenantModules.BankingMicrofinance);
        ProjectTemplateModules.ModuleOf(ProjectAmountKind.Premium).Should().Be(TenantModules.Insurance);
        ProjectTemplateModules.ModuleOf(ProjectAmountKind.Generic).Should().BeNull();
    }

    [Fact]
    public async Task A_credit_template_made_under_the_module_stays_editable_once_it_is_off()
    {
        Guid id;
        await using (var db = _factory.CreateContext())
        {
            var template = ProjectTemplate.Create(_tenantId, "Crédit", null, null, [Step("Étude")], Now,
                new ProjectForecastOptions(AmountKind: ProjectAmountKind.Loan)).Value!;
            db.ProjectTemplates.Add(template);
            await db.SaveChangesAsync();
            id = template.Id;
        }

        async Task<Result> Update(ProjectAmountKind kind)
        {
            await using var db = _factory.CreateContext();
            return await new UpdateProjectTemplateHandler(db, _clock, modules: FixedTenantModules.None)
                .Handle(new UpdateProjectTemplateCommand(id, "Crédit renommé", [Step("Étude")],
                    new ProjectForecastOptions(AmountKind: kind)), CancellationToken.None);
        }

        (await Update(ProjectAmountKind.Loan)).IsSuccess.Should().BeTrue();
        (await Update(ProjectAmountKind.Premium)).Error.Should().Be(TenantModuleErrors.Disabled);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

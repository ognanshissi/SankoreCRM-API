namespace Sankore.Modules.Leads.Tests.Features.ProjectTemplates;

using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.TestMode;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Sandbox lot 4, « Copier en production » for a project template: the product is a blocking
/// dependency, a workflow circuit a reported one, and an existing production template keeps the
/// ids of the steps whose label did not change — the projects standing on them stay there.
/// </summary>
public sealed class PromoteProjectTemplateTests : IDisposable
{
    private readonly Guid _live = Guid.NewGuid();
    private readonly Guid _twin = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly IWorkflowModule _workflow = Substitute.For<IWorkflowModule>();
    private readonly ServiceProvider _services;

    public PromoteProjectTemplateTests()
    {
        var me = Substitute.For<ICurrentUser>();
        me.Id.Returns(Guid.NewGuid());
        me.TenantId.Returns(_twin);
        me.TeamTenantId.Returns(_live);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(LeadsDbContext).Assembly));
        services.AddScoped<ICurrentUser>(_ => BackgroundJobContext.CurrentUser ?? me);
        services.AddScoped<ITenantContext>(_ => BackgroundJobContext.CurrentTenant ?? new FixedTenantContext(_twin));
        services.AddScoped(sp => new LeadsDbContext(
            new DbContextOptionsBuilder<LeadsDbContext>().UseInMemoryDatabase(_database).Options,
            sp.GetRequiredService<ITenantContext>(), LeadsTestEncryptor.Instance));
        services.AddSingleton(_administration);
        services.AddSingleton(_workflow);
        services.AddScoped<ProductionReplay>();
        services.AddScoped<Sankore.Modules.Leads.Features.LeadSources.LeadSourceCopier>();
        services.AddScoped<Sankore.Modules.Leads.Features.ProjectTemplates.ProjectTemplateCopier>();
        _services = services.BuildServiceProvider();

        _workflow.ListActiveCircuitsAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    public void Dispose() => _services.Dispose();

    private void ProductIn(Guid tenantId)
        => _administration.GetProductAsync(tenantId, "CC", Arg.Any<CancellationToken>())
            .Returns(new ProductSummary("CC", "Crédit campagne", "Loan", true, Guid.NewGuid()));

    private static ProjectStepInput Step(string label, OpportunityStage phase, int probability) => new(null, label, phase, probability);

    private async Task<ProjectTemplate> SeedAsync(Guid tenantId, params ProjectStepInput[] steps)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LeadsDbContext>();
        var template = ProjectTemplate.Create(tenantId, "Crédit campagne", null, "CC", steps, DateTimeOffset.UtcNow).Value!;
        db.ProjectTemplates.Add(template);
        await db.SaveChangesAsync();
        return template;
    }

    private async Task<Result<PromotionResult>> PromoteAsync(Guid id, bool dryRun = false)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PromoteProjectTemplateCommand(id, dryRun));
    }

    private async Task<ProjectTemplate?> LiveAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LeadsDbContext>().ProjectTemplates
            .IgnoreQueryFilters().SingleOrDefaultAsync(t => t.TenantId == _live);
    }

    [Fact]
    public async Task A_product_production_lacks_blocks_the_copy_and_is_named_for_the_front()
    {
        ProductIn(_twin);
        var test = await SeedAsync(_twin, Step("Découverte", OpportunityStage.Prospecting, 10));

        var preview = await PromoteAsync(test.Id, dryRun: true);
        preview.Value.Missing.Should().ContainSingle(m => m.Kind == "product" && m.Blocking);

        (await PromoteAsync(test.Id)).Error.Should().Be(PromotionErrors.DependencyMissing);
        (await LiveAsync()).Should().BeNull();
    }

    [Fact]
    public async Task With_the_product_in_production_the_template_is_created_there()
    {
        ProductIn(_twin);
        ProductIn(_live);
        var test = await SeedAsync(_twin, Step("Découverte", OpportunityStage.Prospecting, 10), Step("Offre", OpportunityStage.Proposal, 60));

        var result = await PromoteAsync(test.Id);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Action.Should().Be(PromotionActions.Created);
        (await LiveAsync())!.Steps.Select(s => s.Label).Should().Equal("Découverte", "Offre");
    }

    [Fact]
    public async Task A_step_that_kept_its_label_keeps_its_production_id()
    {
        ProductIn(_twin);
        ProductIn(_live);
        var live = await SeedAsync(_live, Step("Découverte", OpportunityStage.Prospecting, 10), Step("Offre", OpportunityStage.Proposal, 50));
        var kept = live.Steps.Single(s => s.Label == "Offre").Id;
        var test = await SeedAsync(_twin, Step("Découverte", OpportunityStage.Prospecting, 15), Step("Visite", OpportunityStage.NeedsAnalysis, 30),
            Step("offre", OpportunityStage.Proposal, 60));

        var result = await PromoteAsync(test.Id);

        result.Value.Action.Should().Be(PromotionActions.Updated);
        var updated = (await LiveAsync())!;
        updated.Steps.Select(s => s.Label).Should().Equal("Découverte", "Visite", "offre");
        updated.Steps.Single(s => s.Label == "offre").Id.Should().Be(kept);
        updated.Steps.Single(s => s.Label == "offre").Probability.Should().Be(60);
    }
}

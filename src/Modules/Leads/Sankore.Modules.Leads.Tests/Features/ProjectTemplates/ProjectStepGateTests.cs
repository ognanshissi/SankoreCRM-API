namespace Sankore.Modules.Leads.Tests.Features.ProjectTemplates;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ConvertLead;
using Sankore.Modules.Leads.Features.Opportunities.CloseOpportunity;
using Sankore.Modules.Leads.Features.Opportunities.MoveToStep;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Lot 3 : ce qu'un projet doit avoir avant d'atteindre une étape marquée de son gabarit — une
/// fiche client à l'étape « ouvre le dossier client », un KYC validé au palier du produit à
/// l'étape d'approbation — et le produit sans KYC, dont le client naît actif à l'approbation.
///
/// <para>
/// Ce qui est épinglé en priorité : un projet ne se gagne pas en contournant l'étape d'approbation,
/// et un doute sur le KYC (produit inconnu, politique illisible) se lit « KYC exigé ».
/// </para>
/// </summary>
public sealed class ProjectStepGateTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T09:00:00Z");

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _loanLine = Guid.NewGuid();
    private readonly Guid _standardLine = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly IKycModule _kyc = Substitute.For<IKycModule>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedClock(Now);

    public ProjectStepGateTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _user.Id.Returns(Guid.NewGuid());
        _user.TenantId.Returns(_tenantId);

        _administration.ResolveProductLineAsync(_tenantId, "CREDCOM", Arg.Any<CancellationToken>()).Returns(_loanLine);
        _administration.ResolveProductLineAsync(_tenantId, "CARTE", Arg.Any<CancellationToken>()).Returns(_standardLine);
        _administration.ListProductsAsync(_tenantId, null, Arg.Any<CancellationToken>()).Returns(new List<ProductSummary>
        {
            new("CREDCOM", "Crédit commerçant", "Loan", true, _loanLine),
            new("CARTE", "Carte de fidélité", "Standard", true, _standardLine),
        });
        _administration.GetBeneficiaryPolicyAsync(_tenantId, _loanLine, Arg.Any<CancellationToken>())
            .Returns(Policy(_loanLine, "CREDCOM", "Loan", "Full"));
        _administration.GetBeneficiaryPolicyAsync(_tenantId, _standardLine, Arg.Any<CancellationToken>())
            .Returns(Policy(_standardLine, "CARTE", "Standard", KycRequirement.None));
        _administration.GetComplianceSettingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(TestCompleteness.FatcaOff);
    }

    public void Dispose() => _factory.Dispose();

    // ── Les règles du gabarit ───────────────────────────────────────────────

    [Fact]
    public void The_approval_never_comes_before_the_client_file()
    {
        var result = ProjectTemplate.Create(_tenantId, "Crédit", ProductCategory.Loan, null,
        [
            new(null, "Comité", OpportunityStage.Proposal, 60, IsApproval: true),
            new(null, "Pièces", OpportunityStage.Proposal, 70, OpensClientFile: true),
        ], Now);

        result.Error.Should().Be(ProjectTemplateErrors.ApprovalBeforeClientFile);
    }

    [Fact]
    public void A_template_has_one_approval_step_at_most()
    {
        var result = ProjectTemplate.Create(_tenantId, "Crédit", ProductCategory.Loan, null,
        [
            new(null, "Comité", OpportunityStage.Proposal, 60, IsApproval: true),
            new(null, "Accord", OpportunityStage.Negotiation, 80, IsApproval: true),
        ], Now);

        result.Error.Should().Be(ProjectTemplateErrors.TooManyApprovalSteps);
    }

    // ── Produit avec KYC ───────────────────────────────────────────────────

    [Fact]
    public async Task A_kyc_product_needs_a_client_to_reach_the_client_file_step()
    {
        var steps = await SeedLoanTemplate();
        var project = await SeedLeadProject("CREDCOM", convertedTo: null);

        var result = await Move(project, steps[1]);

        result.Error.Should().Be(ProjectTemplateErrors.ClientFileRequired);
        (await Load(project)).StepId.Should().BeNull();
    }

    [Fact]
    public async Task Crossing_the_client_file_step_asks_for_a_kyc_file_when_the_client_has_none()
    {
        var steps = await SeedLoanTemplate();
        var project = await SeedLeadProject("CREDCOM", convertedTo: _clientId);
        _kyc.GetStatusAsync(_tenantId, _clientId, Arg.Any<CancellationToken>()).Returns(KycStatus.NotStarted);

        (await Move(project, steps[1])).IsSuccess.Should().BeTrue();

        var request = _publisher.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<KycRequestedIntegrationEvent>().Single();
        request.CustomerEntityId.Should().Be(_clientId);
        request.ProductLineId.Should().Be(_loanLine);
    }

    [Fact]
    public async Task The_approval_step_waits_for_an_approved_kyc()
    {
        var steps = await SeedLoanTemplate();
        var project = await SeedLeadProject("CREDCOM", convertedTo: _clientId);
        _kyc.GetStatusAsync(_tenantId, _clientId, Arg.Any<CancellationToken>()).Returns(KycStatus.InProgress);

        (await Move(project, steps[2])).Error.Should().Be(ProjectTemplateErrors.KycNotApproved);
    }

    [Fact]
    public async Task The_approval_step_checks_the_tier_the_product_requires()
    {
        var steps = await SeedLoanTemplate();
        var project = await SeedLeadProject("CREDCOM", convertedTo: _clientId);
        _kyc.GetStatusAsync(_tenantId, _clientId, Arg.Any<CancellationToken>()).Returns(KycStatus.Approved);
        _kyc.GetLimitsAsync(_tenantId, _clientId, Arg.Any<CancellationToken>())
            .Returns(new KycLimits("Simplified", true, 0, 0, 30, 80));

        (await Move(project, steps[2])).Error.Should().Be(ProjectTemplateErrors.KycTierInsufficient);

        _kyc.GetLimitsAsync(_tenantId, _clientId, Arg.Any<CancellationToken>())
            .Returns(new KycLimits("Full", false, 0, 0, 30, 80));
        (await Move(project, steps[2])).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_project_cannot_be_won_around_its_approval_step()
    {
        await SeedLoanTemplate();
        var project = await SeedLeadProject("CREDCOM", convertedTo: _clientId);
        _kyc.GetStatusAsync(_tenantId, _clientId, Arg.Any<CancellationToken>()).Returns(KycStatus.InProgress);

        var result = await Close(project, OpportunityStage.ClosedWon);

        result.Error.Should().Be(ProjectTemplateErrors.KycNotApproved);
        (await Load(project)).Stage.Should().NotBe(OpportunityStage.ClosedWon);
        (await Close(project, OpportunityStage.ClosedLost)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_product_reads_as_kyc_required()
    {
        await SeedTemplate(null, null);
        var steps = await StepsOfDefault();
        var project = await SeedLeadProject("", convertedTo: _clientId);
        _kyc.GetStatusAsync(_tenantId, _clientId, Arg.Any<CancellationToken>()).Returns((KycStatus?)null);

        (await Move(project, steps[2])).Error.Should().Be(ProjectTemplateErrors.KycNotApproved);
    }

    // ── Produit sans KYC ───────────────────────────────────────────────────

    [Fact]
    public async Task A_no_kyc_product_passes_the_client_file_step_without_a_client_and_needs_one_to_be_approved()
    {
        var steps = await SeedTemplate(ProductCategory.Standard, null);
        var project = await SeedLeadProject("CARTE", convertedTo: null);

        (await Move(project, steps[1])).IsSuccess.Should().BeTrue();
        (await Move(project, steps[2])).Error.Should().Be(ProjectTemplateErrors.ClientFileRequired);
    }

    [Fact]
    public async Task A_no_kyc_product_is_approved_without_any_kyc_check()
    {
        var steps = await SeedTemplate(ProductCategory.Standard, null);
        var project = await SeedLeadProject("CARTE", convertedTo: _clientId);

        (await Move(project, steps[2])).IsSuccess.Should().BeTrue();
        await _kyc.DidNotReceiveWithAnyArgs().GetStatusAsync(default, default, default);
    }

    [Fact]
    public async Task Converting_on_a_no_kyc_product_creates_an_active_client_and_requests_no_file()
    {
        await using (var db = _factory.CreateContext())
        {
            var lead = LeadTestBuilder.Create().WithTenant(_tenantId).WithAgency(Guid.NewGuid()).WithProduct("CARTE").Build();
            db.Leads.Add(lead);
            db.Opportunities.Add(Opportunity.CreateFromLead(_tenantId, lead.Id, "Projet carte", "CARTE", null, _clock));
            await db.SaveChangesAsync();

            CreateFromLeadRequest? sent = null;
            _customers.CreateFromLeadAsync(Arg.Do<CreateFromLeadRequest>(r => sent = r), Arg.Any<CancellationToken>())
                .Returns(Result.Ok(new CreateFromLeadResult(_clientId, "N-1", AlreadyExisted: false)));

            var handler = new ConvertLeadHandler(
                db, _user, _customers, _administration, TestLeadEmailIndexer.Instance,
                TestCompleteness.Calculator(db, _administration, _customers), _publisher);
            var result = await handler.Handle(new ConvertLeadCommand(lead.Id, Force: true), CancellationToken.None);

            result.IsSuccess.Should().BeTrue(result.Error);
            sent!.KycRequired.Should().BeFalse();
            _publisher.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<KycRequestedIntegrationEvent>()
                .Should().BeEmpty();
        }

        await using var check = _factory.CreateContext();
        (await check.Opportunities.SingleAsync()).CustomerEntityId.Should().Be(_clientId);
    }

    [Fact]
    public async Task A_template_without_marked_steps_gates_nothing()
    {
        var project = await SeedLeadProject("CREDCOM", convertedTo: null);

        (await Move(project, ProjectTemplate.BuiltInDefault[4].Id!.Value)).IsSuccess.Should().BeTrue();
    }

    // ── Outils ──────────────────────────────────────────────────────────────

    private static BeneficiaryPolicyDto Policy(Guid id, string code, string category, string? tier)
        => new(id, code, category, true, true, [], null, null, [], tier, null);

    /// <summary>Pièces (ouvre le dossier client), puis Comité (approbation).</summary>
    private Task<IReadOnlyList<Guid>> SeedLoanTemplate() => SeedTemplate(ProductCategory.Loan, null);

    private async Task<IReadOnlyList<Guid>> SeedTemplate(ProductCategory? category, string? code)
    {
        await using var db = _factory.CreateContext();
        var template = ProjectTemplate.Create(_tenantId, "Parcours", category, code,
        [
            new(null, "Contact", OpportunityStage.Prospecting, 10),
            new(null, "Pièces", OpportunityStage.NeedsAnalysis, 40, OpensClientFile: true),
            new(null, "Comité", OpportunityStage.Proposal, 60, IsApproval: true),
            new(null, "Signature", OpportunityStage.Negotiation, 90),
        ], Now).Value!;
        db.ProjectTemplates.Add(template);
        await db.SaveChangesAsync();
        return template.Steps.Select(s => s.Id).ToList();
    }

    private async Task<IReadOnlyList<Guid>> StepsOfDefault()
    {
        await using var db = _factory.CreateContext();
        var template = await db.ProjectTemplates.SingleAsync(t => t.Category == null && t.ProductCode == null);
        return template.Steps.Select(s => s.Id).ToList();
    }

    private async Task<Guid> SeedLeadProject(string product, Guid? convertedTo)
    {
        await using var db = _factory.CreateContext();
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).WithAgency(Guid.NewGuid()).WithProduct(product).Build();
        if (convertedTo is { } client) lead.Convert(client);
        db.Leads.Add(lead);
        var project = Opportunity.CreateFromLead(_tenantId, lead.Id, "Projet", product, null, _clock);
        db.Opportunities.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private ProjectStepGate Gate(Sankore.Modules.Leads.Infrastructure.LeadsDbContext db)
        => new(db, _administration, _kyc, new FixedTenantContext(_tenantId), _user, _publisher);

    private async Task<Result> Move(Guid projectId, Guid stepId)
    {
        await using var db = _factory.CreateContext();
        return await new MoveOpportunityToStepHandler(
                db, new ProjectTemplateResolver(db, _administration, new FixedTenantContext(_tenantId)), _clock, Gate(db))
            .Handle(new MoveOpportunityToStepCommand(projectId, stepId), CancellationToken.None);
    }

    private async Task<Result> Close(Guid projectId, OpportunityStage outcome)
    {
        await using var db = _factory.CreateContext();
        return await new CloseOpportunityHandler(
                db, _customers, _administration, _user, _clock, NullLogger<CloseOpportunityHandler>.Instance,
                new ProjectTemplateResolver(db, _administration, new FixedTenantContext(_tenantId)), Gate(db))
            .Handle(new CloseOpportunityCommand(projectId, outcome, "motif", null), CancellationToken.None);
    }

    private async Task<Opportunity> Load(Guid id)
    {
        await using var db = _factory.CreateContext();
        return await db.Opportunities.SingleAsync(o => o.Id == id);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

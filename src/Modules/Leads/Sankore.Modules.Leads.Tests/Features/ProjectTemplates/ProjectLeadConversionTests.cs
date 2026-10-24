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
using Sankore.Modules.Leads.Features.ConvertLead.Events;
using Sankore.Modules.Leads.Features.Opportunities.CloseOpportunity;
using Sankore.Modules.Leads.Features.Opportunities.MoveToStep;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// « Un lead est converti lorsqu'un projet a été approuvé » (propriétaire, 2026-10-24) : l'étape
/// « Approbation » atteinte — ou le projet gagné quand son parcours n'en a pas — convertit le lead
/// vers le client dont le dossier a été ouvert plus tôt.
/// </summary>
public sealed class ProjectLeadConversionTests : IDisposable
{
    private const string Product = "CARTE";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T09:00:00Z");

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _line = Guid.NewGuid();
    private readonly Guid _client = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public ProjectLeadConversionTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        _user.Id.Returns(Guid.NewGuid());
        _user.TenantId.Returns(_tenant);

        // A Standard line that requires no KYC: these cases are about the conversion, not the file.
        _admin.ResolveProductLineAsync(_tenant, Product, Arg.Any<CancellationToken>()).Returns(_line);
        _admin.ListProductsAsync(_tenant, null, Arg.Any<CancellationToken>()).Returns(new List<ProductSummary>
        {
            new(Product, "Carte de fidélité", "Standard", true, _line),
        });
        _admin.GetBeneficiaryPolicyAsync(_tenant, _line, Arg.Any<CancellationToken>())
            .Returns(new BeneficiaryPolicyDto(_line, Product, "Standard", true, true, [], null, null, [], KycRequirement.None, null));
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Reaching_the_approval_step_converts_the_lead_onto_its_client()
    {
        var steps = await SeedTemplateAsync(withApproval: true);
        var (leadId, projectId) = await SeedAsync(clientFileOpened: true);

        (await MoveAsync(projectId, steps[1])).IsSuccess.Should().BeTrue();

        var lead = await LeadAsync(leadId);
        lead.Status.Should().Be(LeadStatus.Converted);
        lead.ConvertedToCustomerId.Should().Be(_client);

        var evt = Published<LeadConvertedIntegrationEvent>().Should().ContainSingle().Subject;
        evt.CustomerId.Should().Be(_client);
        evt.ConversionType.Should().Be(LeadConversionType.NewCustomer, "the lead's own file opening created the client");
    }

    [Fact]
    public async Task A_step_before_the_approval_leaves_the_lead_open()
    {
        var steps = await SeedTemplateAsync(withApproval: true, approvalIndex: 2);
        var (leadId, projectId) = await SeedAsync(clientFileOpened: true);

        (await MoveAsync(projectId, steps[1])).IsSuccess.Should().BeTrue();

        (await LeadAsync(leadId)).Status.Should().NotBe(LeadStatus.Converted);
        Published<LeadConvertedIntegrationEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task Winning_converts_the_lead_when_the_template_has_no_approval_step()
    {
        await SeedTemplateAsync(withApproval: false);
        var (leadId, projectId) = await SeedAsync(clientFileOpened: true);

        (await CloseAsync(projectId)).IsSuccess.Should().BeTrue();

        (await LeadAsync(leadId)).Status.Should().Be(LeadStatus.Converted);
    }

    [Fact]
    public async Task Winning_with_no_approval_step_needs_the_client_file()
    {
        // Winning IS the approval there: no client, no win.
        await SeedTemplateAsync(withApproval: false);
        var (leadId, projectId) = await SeedAsync(clientFileOpened: false);

        var result = await CloseAsync(projectId);

        result.Error.Should().Be(ProjectTemplateErrors.ClientFileRequired);
        (await LeadAsync(leadId)).Status.Should().NotBe(LeadStatus.Converted);
    }

    [Fact]
    public async Task A_client_attached_otherwise_is_announced_as_an_existing_customer()
    {
        var steps = await SeedTemplateAsync(withApproval: true);
        var (_, projectId) = await SeedAsync(clientFileOpened: true, reason: "Client reconnu par l'agent");

        await MoveAsync(projectId, steps[1]);

        Published<LeadConvertedIntegrationEvent>().Single().ConversionType
            .Should().Be(LeadConversionType.ExistingCustomerNewProduct);
    }

    [Fact]
    public async Task A_lost_lead_is_not_converted_but_its_project_moves_on()
    {
        var steps = await SeedTemplateAsync(withApproval: true);
        var (leadId, projectId) = await SeedAsync(clientFileOpened: true, lost: true);

        (await MoveAsync(projectId, steps[1])).IsSuccess.Should().BeTrue();

        (await LeadAsync(leadId)).Status.Should().Be(LeadStatus.Lost);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task<List<Guid>> SeedTemplateAsync(bool withApproval, int approvalIndex = 1)
    {
        var steps = new List<ProjectStepInput>
        {
            new(null, "Découverte", OpportunityStage.Prospecting, 10),
            new(null, "Accord", OpportunityStage.Proposal, 60, IsApproval: withApproval && approvalIndex == 1),
            new(null, "Signature", OpportunityStage.Negotiation, 80, IsApproval: withApproval && approvalIndex == 2),
        };
        var template = ProjectTemplate.Create(_tenant, "Standard", ProductCategory.Standard, null, steps, Now).Value;

        await using var db = _factory.CreateContext();
        db.ProjectTemplates.Add(template);
        await db.SaveChangesAsync();
        return template.Steps.Select(s => s.Id).ToList();
    }

    private async Task<(Guid Lead, Guid Project)> SeedAsync(
        bool clientFileOpened, string reason = ConvertLeadHandler.ClientFileOpenedReason, bool lost = false)
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenant).WithAgency(Guid.NewGuid()).WithProduct(Product).Build();
        if (clientFileOpened)
            lead.AttachToCustomer(_client, isAutomatic: false, reason);
        if (lost)
            lead.MarkLost("Injoignable");

        var project = clientFileOpened
            ? Opportunity.CreateForCustomer(_tenant, _client, "Projet", Product, null, TimeProvider.System, leadId: lead.Id)
            : Opportunity.CreateFromLead(_tenant, lead.Id, "Projet", Product, null, TimeProvider.System);

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        db.Opportunities.Add(project);
        await db.SaveChangesAsync();
        return (lead.Id, project.Id);
    }

    private ProjectStepGate Gate(LeadsDbContext db) => new(
        db, _admin, Substitute.For<IKycModule>(), new FixedTenantContext(_tenant), _user, _publisher);

    private ProjectTemplateResolver Resolver(LeadsDbContext db) => new(db, _admin, new FixedTenantContext(_tenant));

    private async Task<Result> MoveAsync(Guid projectId, Guid stepId)
    {
        await using var db = _factory.CreateContext();
        return await new MoveOpportunityToStepHandler(
                db, Resolver(db), TimeProvider.System, Gate(db), new ProjectLeadConversion(db, _publisher))
            .Handle(new MoveOpportunityToStepCommand(projectId, stepId), CancellationToken.None);
    }

    private async Task<Result> CloseAsync(Guid projectId)
    {
        await using var db = _factory.CreateContext();
        var result = await new CloseOpportunityHandler(
                db, Substitute.For<ICustomersModule>(), _admin, _user, TimeProvider.System,
                NullLogger<CloseOpportunityHandler>.Instance, Resolver(db), Gate(db), new ProjectLeadConversion(db, _publisher))
            .Handle(new CloseOpportunityCommand(projectId, OpportunityStage.ClosedWon, "signé", null), CancellationToken.None);
        return result.IsSuccess ? Result.Ok() : Result.Fail(result.Error!);
    }

    private async Task<Lead> LeadAsync(Guid id)
    {
        await using var db = _factory.CreateContext();
        return await db.Leads.SingleAsync(l => l.Id == id);
    }

    private List<T> Published<T>() => _publisher.ReceivedCalls()
        .Select(c => c.GetArguments()[0]).OfType<T>().ToList();
}

namespace Sankore.Modules.Workflow.Tests.Features.Templates;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Features.Templates.Triggers.AddTrigger;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

/// <summary>
/// An internal trigger names an event the platform publishes, about the template's entity. A free
/// name used to be stored and then never matched anything — the editor even offered one
/// (« LEAD_QUALIFIED ») that nothing sent.
/// </summary>
public sealed class AddTriggerHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;

    public AddTriggerHandlerTests() => _factory = new TestWorkflowDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("Lead", "lead.status_changed", true)]
    [InlineData("Lead", "LEAD_CAPTURED", true)]
    [InlineData("Opportunity", "project.closed", true)]
    [InlineData("Account", "client.kyc_validated", true)]
    [InlineData("Lead", "LEAD_QUALIFIED", false)]
    [InlineData("Lead", "client.created", false)]
    [InlineData("Lead", "lead.erased", false)]
    public async Task An_internal_trigger_names_a_published_event_of_the_template_entity(string entityType, string eventName, bool accepted)
    {
        var template = await SeedAsync(entityType);

        var result = await Handler().Handle(new AddTriggerCommand(template.Id, TriggerType.EntityEvent, eventName), default);

        result.IsSuccess.Should().Be(accepted, result.Error);
    }

    [Fact]
    public async Task A_partner_event_stays_free_and_is_lower_cased()
    {
        var template = await SeedAsync("Lead");

        var result = await Handler().Handle(new AddTriggerCommand(template.Id, TriggerType.ExternalEvent, " Appointment.Booked "), default);

        result.IsSuccess.Should().BeTrue();
        await using var db = _factory.CreateContext();
        db.WorkflowTriggers.Single().EventName.Should().Be("appointment.booked");
    }

    private AddTriggerHandler Handler()
    {
        var user = Substitute.For<ICurrentUser>();
        user.TenantId.Returns(_tenantId);
        user.Id.Returns(Guid.NewGuid());
        return new AddTriggerHandler(_factory.CreateContext(), user);
    }

    private async Task<WorkflowTemplate> SeedAsync(string entityType)
    {
        var template = WorkflowTemplate.Create(_tenantId, entityType, "Circuit", Guid.NewGuid());
        template.AddStep(1, "Étape");

        await using var db = _factory.CreateContext();
        db.WorkflowTemplates.Add(template);
        db.WorkflowStepDefinitions.AddRange(template.Steps);
        await db.SaveChangesAsync();
        return template;
    }
}

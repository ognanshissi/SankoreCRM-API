namespace Sankore.Modules.Leads.Tests.Features.Consumers;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CaptureLead.Events;
using Sankore.Modules.Leads.Features.Consumers;
using Sankore.Modules.Leads.Features.Tasks;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

/// <summary>A lead its agent typed in is never dispatched: it still owes a « Premier contact ».</summary>
public sealed class OwnedLeadFirstContactConsumerTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public OwnedLeadFirstContactConsumerTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> SeedAsync(bool owned = true, bool contacted = false)
    {
        await using var db = _factory.CreateContext();
        var lead = LeadTestBuilder.Create().WithTenant(_tenant).Fresh().Build();
        if (owned) lead.SetOwner(_owner);
        if (contacted) lead.RecordContactEstablished();
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead.Id;
    }

    private async Task ConsumeAsync(Guid leadId, bool hasOwner = true)
    {
        var services = new ServiceCollection();
        services.AddScoped<LeadsDbContext>(_ => _factory.CreateContext());
        services.AddSingleton(TimeProvider.System);
        await using var provider = services.BuildServiceProvider();

        var context = Substitute.For<ConsumeContext<LeadCapturedEvent>>();
        context.Message.Returns(new LeadCapturedEvent(leadId, _tenant, "Agency", hasOwner, false));
        await new OwnedLeadFirstContactConsumer(provider.GetRequiredService<IServiceScopeFactory>()).Consume(context);
    }

    private async Task<List<CrmTask>> TasksAsync(Guid leadId)
    {
        await using var db = _factory.CreateContext();
        return await db.CrmTasks.Where(t => t.LeadId == leadId).ToListAsync();
    }

    [Fact]
    public async Task The_owner_gets_the_first_contact_task_due_at_the_rule_delay()
    {
        var id = await SeedAsync();

        await ConsumeAsync(id);

        var task = (await TasksAsync(id)).Should().ContainSingle().Subject;
        task.Type.Should().Be(CrmTaskType.FirstContact);
        task.AssignedAgentId.Should().Be(_owner);
        task.TriggerEventType.Should().Be(TaskTriggerEvents.LeadDispatched);
        task.SlaDeadline.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task A_redelivered_event_does_not_stack_a_second_task()
    {
        var id = await SeedAsync();

        await ConsumeAsync(id);
        await ConsumeAsync(id);

        (await TasksAsync(id)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_lead_captured_without_owner_is_the_dispatchs()
    {
        var id = await SeedAsync(owned: false);

        await ConsumeAsync(id, hasOwner: false);

        (await TasksAsync(id)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_lead_already_contacted_owes_nothing()
    {
        var id = await SeedAsync(contacted: true);

        await ConsumeAsync(id);

        (await TasksAsync(id)).Should().BeEmpty();
    }
}

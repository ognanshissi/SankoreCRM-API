namespace Sankore.Modules.Leads.Tests.Lifecycle;

using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.PublicApi.Events;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Outbox;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// The lead and project lifecycle reaches the outbox from the domain events, at SaveChanges, with
/// no handler involved. Before this, a status change, an assignment or a project step was raised
/// on the aggregate and dropped: nothing outside the handler could learn of it, which is what an
/// outbound webhook needs.
/// </summary>
public sealed class LeadLifecycleEventsTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private readonly TestDbContextFactory _factory = new(TenantId);

    [Fact]
    public async Task A_captured_lead_is_announced_once_with_no_personal_value()
    {
        await using var db = _factory.CreateContext();
        var lead = Lead.Capture(
            tenantId: TenantId, fullName: "Awa Ndiaye", phoneNumber: "+221771234567", source: LeadSource.Web,
            interestedProduct: "Crédit", preferredLanguage: "fr", location: new GeoPoint(14.69, -17.44), preferredAgencyId: null,
            clock: TimeProvider.System);
        db.Leads.Add(lead);

        await db.SaveChangesAsync();
        await db.SaveChangesAsync(); // a second save of the same context announces nothing again

        var rows = await Rows(db);
        rows.Should().ContainSingle(r => r.EventType.Contains(nameof(LeadCreatedEvent)));

        var created = Read<LeadCreatedEvent>(rows.Single(r => r.EventType.Contains(nameof(LeadCreatedEvent))));
        created.TenantId.Should().Be(TenantId);
        created.LeadId.Should().Be(lead.Id);
        created.Source.Should().Be("Web");

        // The payload is what leaves the module; the name and the number must not be in it.
        string.Join("\n", rows.Select(r => r.PayloadJson)).Should()
            .NotContain("Awa").And.NotContain("Ndiaye").And.NotContain("771234567");
    }

    [Fact]
    public async Task Closing_then_reopening_announces_the_change_and_the_transition()
    {
        var lead = await SavedLeadAsync();

        await using (var db = _factory.CreateContext())
        {
            var tracked = await db.Leads.AsTracking().SingleAsync(l => l.Id == lead.Id);
            tracked.Close(LeadCloseReason.Lost, "Le client a choisi un concurrent").IsSuccess.Should().BeTrue();
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateContext())
        {
            var tracked = await db.Leads.AsTracking().SingleAsync(l => l.Id == lead.Id);
            tracked.Reopen().IsSuccess.Should().BeTrue();
            await db.SaveChangesAsync();

            var rows = await Rows(db);
            Read<LeadClosedEvent>(Single<LeadClosedEvent>(rows)).Status.Should().Be("Lost");
            Single<LeadReopenedEvent>(rows).Should().NotBeNull();
            rows.Count(r => r.EventType.Contains(nameof(LeadStatusChangedEvent))).Should().Be(2);

            // The agent's motive is free text and stays in the CRM.
            rows.Should().NotContain(r => r.PayloadJson.Contains("concurrent"));
        }
    }

    [Fact]
    public async Task A_conversion_is_announced_once_with_its_client()
    {
        var lead = await SavedLeadAsync();
        var clientId = Guid.NewGuid();

        await using var db = _factory.CreateContext();
        var tracked = await db.Leads.AsTracking().SingleAsync(l => l.Id == lead.Id);
        tracked.Convert(clientId).IsSuccess.Should().BeTrue();
        tracked.Convert(clientId).IsSuccess.Should().BeTrue(); // idempotent: raises nothing more
        await db.SaveChangesAsync();

        var rows = await Rows(db);
        Read<LeadConvertedToClientEvent>(Single<LeadConvertedToClientEvent>(rows)).ClientId.Should().Be(clientId);
        rows.Should().NotContain(r => r.EventType.Contains(nameof(LeadClosedEvent)));
    }

    [Fact]
    public async Task Qualification_is_announced_only_when_the_lead_reaches_Qualified()
    {
        var lead = LeadTestBuilder.Create().WithTenant(TenantId).Fresh().Build();

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        lead.Qualify(40, qualifiedThreshold: 60);   // Qualifying: a status change, not a qualification
        await db.SaveChangesAsync();
        (await Rows(db)).Should().NotContain(r => r.EventType.Contains(nameof(LeadQualifiedEvent)));

        lead.Qualify(80, qualifiedThreshold: 60);
        await db.SaveChangesAsync();

        var rows = await Rows(db);
        Read<LeadQualifiedEvent>(Single<LeadQualifiedEvent>(rows)).Score.Should().Be(80);
        rows.Select(r => r.EventType).Count(t => t.Contains(nameof(LeadStatusChangedEvent))).Should().Be(2);
    }

    [Fact]
    public async Task A_new_owner_is_announced_as_an_assignment()
    {
        var lead = await SavedLeadAsync();
        var owner = Guid.NewGuid();

        await using var db = _factory.CreateContext();
        var tracked = await db.Leads.AsTracking().SingleAsync(l => l.Id == lead.Id);
        tracked.SetOwner(owner).IsSuccess.Should().BeTrue();
        await db.SaveChangesAsync();

        var assigned = Read<LeadAssignedEvent>(Single<LeadAssignedEvent>(await Rows(db)));
        assigned.UserId.Should().Be(owner);
        assigned.Method.Should().Be("Owner");
    }

    [Fact]
    public async Task A_project_is_announced_at_creation_on_each_step_and_at_closing()
    {
        var leadId = Guid.NewGuid();
        var step = Guid.NewGuid();

        await using var db = _factory.CreateContext();
        var project = Opportunity.CreateFromLead(
            TenantId, leadId, "Crédit tracteur", "CRED-AGRI", null, TimeProvider.System,
            estimatedAmount: new Money(2_000_000m, "XOF"));
        db.Opportunities.Add(project);
        await db.SaveChangesAsync();

        project.MoveToStep(step, OpportunityStage.Proposal, 50, DateTimeOffset.UtcNow).IsSuccess.Should().BeTrue();
        project.MoveToStep(step, OpportunityStage.Proposal, 50, DateTimeOffset.UtcNow); // same step: nothing
        await db.SaveChangesAsync();

        project.Close(OpportunityStage.ClosedWon, DateTimeOffset.UtcNow, "Signé en agence").IsSuccess.Should().BeTrue();
        await db.SaveChangesAsync();

        var rows = await Rows(db);
        var created = Read<ProjectCreatedEvent>(Single<ProjectCreatedEvent>(rows));
        created.LeadId.Should().Be(leadId);
        created.Amount.Should().Be(2_000_000m);
        created.Currency.Should().Be("XOF");

        var moved = Read<ProjectStageChangedEvent>(Single<ProjectStageChangedEvent>(rows));
        moved.Stage.Should().Be("Proposal");
        moved.StepId.Should().Be(step);

        Read<ProjectClosedEvent>(Single<ProjectClosedEvent>(rows)).Outcome.Should().Be("Won");
        rows.Should().NotContain(r => r.PayloadJson.Contains("Signé"));
    }

    [Fact]
    public async Task An_aggregate_that_is_not_saved_announces_nothing()
    {
        await using var db = _factory.CreateContext();
        LeadTestBuilder.Create().WithTenant(TenantId).Build().Close(LeadCloseReason.Lost);

        await db.SaveChangesAsync();

        (await Rows(db)).Should().BeEmpty();
    }

    private async Task<Lead> SavedLeadAsync()
    {
        var lead = LeadTestBuilder.Create().WithTenant(TenantId).Build();
        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        db.OutboxMessages.RemoveRange(await db.OutboxMessages.ToListAsync());
        await db.SaveChangesAsync();
        return lead;
    }

    private static Task<List<OutboxMessage>> Rows(LeadsDbContext db)
        => db.OutboxMessages.AsNoTracking().OrderBy(m => m.OccurredAt).ToListAsync();

    private static OutboxMessage Single<TEvent>(IEnumerable<OutboxMessage> rows)
        => rows.Should().ContainSingle(r => r.EventType.StartsWith(typeof(TEvent).FullName + ",")).Subject;

    private static TEvent Read<TEvent>(OutboxMessage row)
    {
        // Through the type the processor will resolve, as the processor does.
        var type = Type.GetType(row.EventType);
        type.Should().Be(typeof(TEvent));
        return (TEvent)JsonSerializer.Deserialize(row.PayloadJson, type!, OutboxJson.Options)!;
    }

    public void Dispose() => _factory.Dispose();
}

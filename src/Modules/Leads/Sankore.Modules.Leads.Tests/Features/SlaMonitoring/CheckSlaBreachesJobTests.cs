namespace Sankore.Modules.Leads.Tests.Features.SlaMonitoring;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.SlaMonitoring;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// The SLA job, which had no coverage at all.
///
/// <para>
/// What is pinned is the shape of a CLOSED assignment. The job used to stop re-processing a
/// breached row by calling <c>RecordFirstContact(now)</c> on it — recording a call that never
/// happened, which then counted as a late contact in <c>GetAgentPerformance</c> — while the
/// escalated row it created never became the lead's current assignment and therefore could never
/// be closed by anything at all. Both halves are superseding now.
/// </para>
/// </summary>
public sealed class CheckSlaBreachesJobTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();
    private readonly TestDbFactory _db;
    private readonly INotificationsModule _notifications = Substitute.For<INotificationsModule>();
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    public CheckSlaBreachesJobTests()
    {
        _db = new TestDbFactory(_tenantId);

        _admin.GetAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new AgentSummary(
                _agentId, "Awa Ouattara", Guid.NewGuid(), ["fr"], ["Loan"], null, 1, 0, 0.2, true));

        // A supervisor exists, so escalation is allowed to proceed.
        _admin.GetTeamAgentIdsAsync(_tenantId, _agentId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { Guid.NewGuid() });

        _notifications.QueueEmailAsync(Arg.Any<QueueEmailRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(Guid.NewGuid()));

        // The alert now goes through M12, which owns the agent's address and locale. The job used
        // to queue in M08 itself, against a synthetic address nobody receives.
        _admin.NotifyUserAsync(Arg.Any<NotifyUserRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    public void Dispose() => _db.Dispose();

    /// <summary>Shares one InMemory database between the job's scope and the assertions.</summary>
    private sealed class TestDbFactory(Guid tenantId) : IDisposable
    {
        private readonly string _name = $"sla-tests-{Guid.NewGuid()}";

        public LeadsDbContext Create() => new(
            new DbContextOptionsBuilder<LeadsDbContext>().UseInMemoryDatabase(_name).Options,
            new FixedTenantContext(tenantId),
            TestSupport.LeadsTestEncryptor.Instance);

        public void Dispose() { }
    }

    private CheckSlaBreachesJob Job()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.Create());
        services.AddSingleton(_admin);
        services.AddSingleton(_notifications);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddLogging();

        return new CheckSlaBreachesJob(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    private async Task<(Lead Lead, LeadAssignment Assignment)> SeedBreachedAsync()
    {
        var lead = Lead.Capture(
            tenantId: _tenantId,
            fullName: "Awa Ouattara",
            phoneNumber: "+2250708091801",
            source: LeadSource.Web,
            interestedProduct: "Crédit commerçant",
            preferredLanguage: "FR",
            location: new GeoPoint(5.3, -4.0),
            preferredAgencyId: null,
            clock: TimeProvider.System);

        var assignment = LeadAssignment.Create(
            tenantId: _tenantId,
            leadId: lead.Id,
            agentId: _agentId,
            strategy: DispatchingStrategy.RoundRobin,
            compatibilityScore: 42,
            // Already past: the job must see it as breached.
            slaDeadline: DateTimeOffset.UtcNow.AddHours(-3),
            createdAt: DateTimeOffset.UtcNow.AddHours(-5));

        lead.AssignTo(assignment, currentAssignment: null);

        await using var db = _db.Create();
        db.Leads.Add(lead);
        db.LeadAssignments.Add(assignment);
        await db.SaveChangesAsync();

        return (lead, assignment);
    }

    [Fact]
    public async Task An_escalation_supersedes_the_breached_assignment_without_faking_a_contact()
    {
        var (lead, assignment) = await SeedBreachedAsync();

        await Job().ExecuteAsync(_tenantId);

        await using var db = _db.Create();
        var breached = await db.LeadAssignments.FirstAsync(a => a.Id == assignment.Id);

        breached.SupersededAt.Should().NotBeNull();
        breached.FirstContactAt.Should().BeNull(
            "the agent never called; recording one here credited them with a late contact");

        var escalated = await db.LeadAssignments
            .FirstAsync(a => a.Id != assignment.Id && a.LeadId == lead.Id);
        escalated.SupersededAt.Should().BeNull();

        var stored = await db.Leads.FirstAsync(l => l.Id == lead.Id);
        stored.CurrentAssignmentId.Should().Be(escalated.Id,
            "an escalated row that is not the lead's current assignment can never be closed");
    }

    [Fact]
    public async Task A_superseded_assignment_is_never_alerted_on_again()
    {
        // The whole point: before this, each run re-found the same stranded row and mailed its
        // former agent again, every day, for as long as the lead stayed open.
        await SeedBreachedAsync();

        await Job().ExecuteAsync(_tenantId);
        _admin.ClearReceivedCalls();

        // The escalated row's own deadline is in the future, so the second run has nothing to do.
        await Job().ExecuteAsync(_tenantId);

        await _admin.DidNotReceive().NotifyUserAsync(
            Arg.Any<NotifyUserRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_breach_alerts_the_assigned_agent_once()
    {
        await SeedBreachedAsync();

        await Job().ExecuteAsync(_tenantId);

        await _admin.Received(1).NotifyUserAsync(
            Arg.Is<NotifyUserRequest>(r =>
                r.TemplateKey == "lead.sla-breach"
                && r.TenantId == _tenantId
                && r.UserId == _agentId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_alert_carries_the_variables_the_template_actually_reads()
    {
        // Snake_case, not camelCase. The job used to pass leadName / agentName / slaDeadline
        // against a template reading {{ lead_name }} / {{ full_name }} / {{ sla_deadline }}, so
        // every placeholder rendered empty — a mismatch that fails by producing nothing rather
        // than by raising, which is why nothing caught it. The recipient's own name is NOT among
        // them: M12 adds full_name.
        await SeedBreachedAsync();

        await Job().ExecuteAsync(_tenantId);

        await _admin.Received(1).NotifyUserAsync(
            Arg.Is<NotifyUserRequest>(r =>
                r.TemplateData != null
                && r.TemplateData.ContainsKey("lead_name")
                && r.TemplateData.ContainsKey("lead_id")
                && r.TemplateData.ContainsKey("sla_deadline")
                && r.TemplateData.ContainsKey("breach_hours")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_agent_with_no_address_does_not_stop_the_escalation()
    {
        // NotifyUserAsync returning false is an ordinary outcome — a deactivated agent, a user
        // with no email. The escalation is the half that protects the lead, and it must happen
        // whether or not anybody could be told.
        _admin.NotifyUserAsync(Arg.Any<NotifyUserRequest>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var (lead, assignment) = await SeedBreachedAsync();

        await Job().ExecuteAsync(_tenantId);

        await using var db = _db.Create();
        var escalated = await db.LeadAssignments
            .FirstOrDefaultAsync(a => a.Id != assignment.Id && a.LeadId == lead.Id);

        escalated.Should().NotBeNull();
    }
}

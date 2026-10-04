namespace Sankore.Modules.Leads.Tests.Features.DispatchLead;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Dispatching used to demand <c>Qualified</c>. It now accepts any live lead: routing a captured
/// lead to an agent is precisely how it gets qualified. Only the terminal statuses refuse —
/// there is nobody to work a lead that is converted, lost, disqualified or archived.
/// </summary>
public sealed class LeadDispatchabilityTests
{
    private static Lead Fresh() => Lead.Capture(
        tenantId: Guid.NewGuid(),
        fullName: "Awa Ouattara",
        phoneNumber: "+2250708091801",
        source: LeadSource.Web,
        interestedProduct: "Crédit commerçant",
        preferredLanguage: "FR",
        location: new GeoPoint(5.3, -4.0),
        preferredAgencyId: null,
        clock: TimeProvider.System);

    private static LeadAssignment AnAssignment(Lead lead, Guid? ruleId = null) =>
        LeadAssignment.Create(
            tenantId: lead.TenantId,
            leadId: lead.Id,
            agentId: Guid.NewGuid(),
            strategy: DispatchingStrategy.RoundRobin,
            compatibilityScore: 42,
            slaDeadline: DateTimeOffset.UtcNow.AddHours(2),
            createdAt: DateTimeOffset.UtcNow,
            ruleId: ruleId);

    [Fact]
    public void A_newly_captured_lead_can_be_dispatched()
    {
        var lead = Fresh();
        lead.Status.Should().Be(LeadStatus.New);

        lead.IsDispatchable.Should().BeTrue();
        lead.AssignTo(AnAssignment(lead), currentAssignment: null).IsSuccess.Should().BeTrue();
        lead.CurrentAssignedId.Should().NotBeNull();
    }

    [Fact]
    public void A_lead_still_being_qualified_can_be_dispatched()
    {
        var lead = Fresh();
        lead.Qualify(30);   // → Qualifying, below the default threshold
        lead.Status.Should().Be(LeadStatus.Qualifying);

        lead.AssignTo(AnAssignment(lead), currentAssignment: null).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(LeadCloseReason.Lost)]
    [InlineData(LeadCloseReason.Disqualified)]
    [InlineData(LeadCloseReason.Archived)]
    public void A_closed_lead_refuses_dispatching(LeadCloseReason reason)
    {
        var lead = Fresh();
        lead.Close(reason);

        lead.IsDispatchable.Should().BeFalse();

        var result = lead.AssignTo(AnAssignment(lead), currentAssignment: null);
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("LEAD_NOT_DISPATCHABLE");
        lead.CurrentAssignedId.Should().BeNull();
    }

    [Fact]
    public void An_assignment_records_the_rule_that_produced_it()
    {
        // Without it nobody can tell afterwards which configuration routed a lead.
        var lead = Fresh();
        var ruleId = Guid.NewGuid();

        var assignment = AnAssignment(lead, ruleId);

        assignment.RuleId.Should().Be(ruleId);
    }

    [Fact]
    public void An_assignment_made_with_the_built_in_defaults_records_no_rule()
    {
        AnAssignment(Fresh()).RuleId.Should().BeNull();
    }

    // ── one open assignment per lead ────────────────────────────────────────
    //
    // A replaced assignment can never be closed by RecordFirstContact, which only ever stamps
    // Lead.CurrentAssignmentId. Left open it kept breaching its SLA for ever: CheckSlaBreachesJob
    // scans the rows, not the lead's pointer, so the previous agent was mailed an alert every day
    // the lead stayed open, and the row counted as "not contacted" in their performance.

    [Fact]
    public void Reassigning_supersedes_the_previous_assignment()
    {
        var lead = Fresh();
        var first = AnAssignment(lead);
        lead.AssignTo(first, currentAssignment: null);

        var second = AnAssignment(lead);
        lead.AssignTo(second, currentAssignment: first).IsSuccess.Should().BeTrue();

        first.IsSuperseded.Should().BeTrue();
        first.HasBreachedSla(DateTimeOffset.UtcNow.AddYears(1)).Should().BeFalse(
            "nobody owes a first contact on a lead that was taken off them");
        second.IsSuperseded.Should().BeFalse();
        lead.CurrentAssignmentId.Should().Be(second.Id);
    }

    [Fact]
    public void Returning_to_the_queue_supersedes_the_assignment_given_up()
    {
        var lead = Fresh();
        var assignment = AnAssignment(lead);
        lead.AssignTo(assignment, currentAssignment: null);

        lead.ReturnToQueue(assignment).IsSuccess.Should().BeTrue();

        assignment.IsSuperseded.Should().BeTrue();
        lead.CurrentAssignmentId.Should().BeNull();
        lead.CurrentAssignedId.Should().BeNull();
        lead.Status.Should().Be(LeadStatus.Qualified);
    }

    [Fact]
    public void Superseding_does_not_pretend_the_agent_called()
    {
        // Flattening "taken off them" into a first contact would silently improve every agent's
        // contact rate, which is the opposite of the point.
        var lead = Fresh();
        var first = AnAssignment(lead);
        lead.AssignTo(first, currentAssignment: null);
        lead.AssignTo(AnAssignment(lead), currentAssignment: first);

        first.FirstContactAt.Should().BeNull();
    }

    [Fact]
    public void Replacing_an_assignment_without_handing_it_over_is_refused()
    {
        // The aggregate holds no navigation to its assignments, so the caller must supply the row.
        // Failing loudly is the whole guarantee: a silent skip is the bug this closes.
        var lead = Fresh();
        lead.AssignTo(AnAssignment(lead), currentAssignment: null);

        var result = lead.AssignTo(AnAssignment(lead), currentAssignment: null);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("CURRENT_ASSIGNMENT_REQUIRED");
    }

    [Fact]
    public void Handing_over_the_wrong_assignment_is_refused()
    {
        var lead = Fresh();
        var current = AnAssignment(lead);
        lead.AssignTo(current, currentAssignment: null);

        var somebodyElses = AnAssignment(lead);
        var result = lead.AssignTo(AnAssignment(lead), currentAssignment: somebodyElses);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("CURRENT_ASSIGNMENT_MISMATCH");
        somebodyElses.IsSuperseded.Should().BeFalse("the wrong row must not be stamped");
        current.IsSuperseded.Should().BeFalse();
    }

    [Fact]
    public void Superseding_twice_keeps_the_first_moment()
    {
        var lead = Fresh();
        var assignment = AnAssignment(lead);
        lead.AssignTo(assignment, currentAssignment: null);
        lead.ReturnToQueue(assignment);

        var stamped = assignment.SupersededAt;
        assignment.Supersede(DateTimeOffset.UtcNow.AddHours(1));

        assignment.SupersededAt.Should().Be(stamped);
    }
}

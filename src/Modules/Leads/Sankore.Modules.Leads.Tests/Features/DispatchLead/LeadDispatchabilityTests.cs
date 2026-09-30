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
        lead.AssignTo(AnAssignment(lead)).IsSuccess.Should().BeTrue();
        lead.CurrentAssignedId.Should().NotBeNull();
    }

    [Fact]
    public void A_lead_still_being_qualified_can_be_dispatched()
    {
        var lead = Fresh();
        lead.Qualify(30);   // → Qualifying, below the default threshold
        lead.Status.Should().Be(LeadStatus.Qualifying);

        lead.AssignTo(AnAssignment(lead)).IsSuccess.Should().BeTrue();
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

        var result = lead.AssignTo(AnAssignment(lead));
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
}

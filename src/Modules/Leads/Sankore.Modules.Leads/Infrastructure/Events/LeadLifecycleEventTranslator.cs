namespace Sankore.Modules.Leads.Infrastructure.Events;

using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Domain.Events;
using Sankore.Modules.Leads.PublicApi.Events;
using Sankore.Shared.Kernel;

/// <summary>
/// Turns the domain events a <see cref="Lead"/> or an <see cref="Opportunity"/> raised into the
/// public lifecycle events of <c>Leads.PublicApi</c> (see <c>LeadLifecycleEvents.cs</c>). Pure:
/// no I/O, no clock — <see cref="LeadsDbContext"/> calls it at SaveChanges and writes the result
/// to the outbox in the same transaction.
///
/// <para>
/// What comes from the domain event is what changed; what comes from the aggregate is what it is
/// (its tenant, source, agency). A domain event says nothing a webhook would not be allowed to
/// carry, and no personal value is read off the aggregate here.
/// </para>
/// </summary>
internal static class LeadLifecycleEventTranslator
{
    /// <summary>The statuses a lead is CLOSED in. Converted is an outcome of its own, announced as such.</summary>
    private static readonly HashSet<LeadStatus> Closed = [LeadStatus.Lost, LeadStatus.Disqualified, LeadStatus.Archived];

    public static IEnumerable<IIntegrationEvent> Translate(AggregateRoot aggregate, IDomainEvent raised)
        => aggregate switch
        {
            Lead lead => FromLead(lead, raised),
            Opportunity project => FromProject(project, raised),
            _ => [],
        };

    private static IEnumerable<IIntegrationEvent> FromLead(Lead lead, IDomainEvent raised)
    {
        var t = lead.TenantId;

        switch (raised)
        {
            case LeadCapturedDomainEvent:
                yield return new LeadCreatedEvent(
                    t, lead.Id, lead.Status.ToString(), lead.Source.ToString(),
                    lead.LeadSourceConfigId, lead.OwnerId, lead.AgencyId, lead.CustomerId);
                break;

            case LeadAssignedDomainEvent e:
                yield return new LeadAssignedEvent(t, e.LeadId, e.AgentId, null, e.AssignmentId, "Dispatch");
                break;

            case LeadOwnerChangedDomainEvent e when e.PreviousOwnerId != e.NewOwnerId:
                yield return new LeadAssignedEvent(t, e.LeadId, e.NewOwnerId, e.PreviousOwnerId, null, "Owner");
                break;

            case LeadStatusChangedDomainEvent e when e.Previous != e.New:
                yield return new LeadStatusChangedEvent(t, e.LeadId, e.Previous.ToString(), e.New.ToString());

                if (e.New == LeadStatus.Converted)
                    // Read off the aggregate rather than LeadConvertedDomainEvent: the legacy
                    // Convert() raises only the status change, and both must be announced once.
                    yield return new LeadConvertedToClientEvent(t, e.LeadId, lead.ConvertedToCustomerId);
                else if (Closed.Contains(e.New) && !Closed.Contains(e.Previous))
                    yield return new LeadClosedEvent(t, e.LeadId, e.New.ToString());
                else if (Closed.Contains(e.Previous) && !Closed.Contains(e.New))
                    yield return new LeadReopenedEvent(t, e.LeadId, e.Previous.ToString(), e.New.ToString());
                break;

            case LeadPipelineStageChangedDomainEvent e when e.Previous != e.New:
                yield return new LeadStageChangedEvent(t, e.LeadId, e.Previous.ToString(), e.New.ToString());
                break;

            // Raised for every scoring; only reaching Qualified is the event a partner waits for.
            case LeadQualifiedDomainEvent e when e.NewStatus == LeadStatus.Qualified:
                yield return new LeadQualifiedEvent(t, e.LeadId, e.Score);
                break;

            case LeadMergedDomainEvent e:
                yield return new LeadMergedEvent(t, e.SourceLeadId, e.TargetLeadId);
                break;

            case ConsentWithdrawnDomainEvent e:
                yield return new LeadConsentWithdrawnEvent(t, e.LeadId, e.ConsentType);
                break;
        }
    }

    private static IEnumerable<IIntegrationEvent> FromProject(Opportunity project, IDomainEvent raised)
    {
        var t = project.TenantId;

        switch (raised)
        {
            case OpportunityCreatedDomainEvent:
                yield return new ProjectCreatedEvent(
                    t, project.Id, project.LeadId, project.CustomerEntityId, project.Stage.ToString(),
                    project.ProductLineId, project.EstimatedAmount?.Amount, project.EstimatedAmount?.Currency,
                    project.OwnerId);
                break;

            case OpportunityStageChangedDomainEvent e:
                yield return new ProjectStageChangedEvent(
                    t, project.Id, project.LeadId, project.CustomerEntityId,
                    e.PreviousStage.ToString(), e.NewStage.ToString(), e.PreviousStepId, e.NewStepId);
                break;

            case OpportunityClosedDomainEvent e:
                yield return new ProjectClosedEvent(
                    t, project.Id, project.LeadId, project.CustomerEntityId,
                    e.Outcome == OpportunityStage.ClosedWon ? "Won" : "Lost",
                    project.EstimatedAmount?.Amount, project.EstimatedAmount?.Currency);
                break;
        }
    }
}

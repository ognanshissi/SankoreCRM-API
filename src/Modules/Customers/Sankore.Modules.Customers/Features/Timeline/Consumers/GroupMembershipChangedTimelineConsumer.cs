namespace Sankore.Modules.Customers.Features.Timeline.Consumers;

using Sankore.Modules.Customers.Features.Timeline.Projection;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;

/// <summary>
/// Projects a solidarity-group / tontine / VSLA membership change onto the MEMBER's timeline.
/// The group's own audit trail lives in <c>group_memberships</c>; what belongs here is the
/// member-side fact, because that is what an advisor reads when opening a client file.
/// </summary>
public sealed class GroupMembershipChangedTimelineConsumer(
    IInboxGuard inbox,
    IClientTimelineProjector projector) : TimelineConsumerBase<GroupMembershipChangedEvent>(inbox)
{
    protected override Guid TenantIdOf(GroupMembershipChangedEvent evt) => evt.TenantId;

    protected override Task ProjectAsync(GroupMembershipChangedEvent evt, CancellationToken ct)
    {
        var summary = evt.Change switch
        {
            "Joined" => $"Adhésion au groupe {evt.GroupId:D} — rôle {evt.OfficeRole}",
            "Left" => $"Sortie du groupe {evt.GroupId:D}",
            "RoleChanged" => $"Rôle de bureau modifié dans le groupe {evt.GroupId:D} — {evt.OfficeRole}",
            _ => $"Adhésion au groupe {evt.GroupId:D} modifiée ({evt.Change}) — rôle {evt.OfficeRole}",
        };

        return projector.AppendAsync(
            tenantId: evt.TenantId,
            clientId: evt.ClientId,
            sourceModule: TimelineSourceModules.Customers,
            entryType: ClientTimelineEntryTypes.GroupMembershipChanged,
            occurredAt: evt.OccurredAt,
            summary: summary,
            referenceType: "ClientGroup",
            referenceId: evt.GroupId.ToString("D"),
            dedupKey: TimelineDedupKey.ForEvent(
                TimelineSourceModules.Customers, ClientTimelineEntryTypes.GroupMembershipChanged, evt.EventId),
            ct: ct);
    }
}

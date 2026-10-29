namespace Sankore.Shared.Infrastructure.Workflow;

using Sankore.Shared.Kernel;

/// <summary>
/// Bus message published by any module to start an event-driven workflow.
/// The Workflow module listens for this signal and auto-starts instances
/// for every active trigger whose EventName match.
///
/// <para>
/// An integration event (webhooks lot 6) so it can go through a module's outbox, in the
/// transaction of what caused it — the external-event endpoint does; <c>CaptureLeadHandler</c>
/// still publishes it on the bus directly.
/// </para>
/// </summary>
public sealed record WorkflowTriggerSignal(
    Guid TenantId,
    string EntityType,
    Guid EntityId,
    string EventName,
    Dictionary<string, object>? Context = null) : IntegrationEventBase
{
    /// <summary>
    /// A partner's event (<c>TriggerType.ExternalEvent</c>) rather than one of the CRM's own
    /// (<c>EntityEvent</c>): the two namespaces of event names never mix.
    /// </summary>
    public bool IsExternal { get; init; }
}

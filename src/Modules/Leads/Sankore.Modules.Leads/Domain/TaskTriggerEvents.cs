namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Canonical string keys used in <see cref="TaskGenerationRule.TriggerEventType"/>
/// and in MassTransit consumers to match rules to events (US-M13-080).
/// </summary>
public static class TaskTriggerEvents
{
    public const string LeadDispatched             = "LeadDispatched";
    public const string LeadDispatchingFailed      = "LeadDispatchingFailed";
    public const string LeadScoreCriticallyChanged = "LeadScoreCriticallyChanged";
}

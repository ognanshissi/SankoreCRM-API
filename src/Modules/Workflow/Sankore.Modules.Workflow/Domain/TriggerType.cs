namespace Sankore.Modules.Workflow.Domain;

public enum TriggerType
{
    /// <summary>Fired when a business domain event occurs (e.g., LEAD_CAPTURED).</summary>
    EntityEvent,

    /// <summary>Fired on a cron-like schedule (Phase N+ — stub in current release).</summary>
    Schedule,

    /// <summary>Fired from an external system via the trigger API or webhook.</summary>
    ExternalEvent
}

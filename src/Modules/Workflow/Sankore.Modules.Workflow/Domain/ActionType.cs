namespace Sankore.Modules.Workflow.Domain;

/// <summary>
/// Side-effect actions that fire after a workflow transition is matched.
/// </summary>
public enum ActionType
{
    /// <summary>Assigns the entity to a specific user (UserId in config).</summary>
    AssignUser,

    /// <summary>Assigns the entity to the next user in a round-robin pool (RoleCode in config).</summary>
    AssignRoundRobin,

    /// <summary>Sends an in-app notification to a role or user (Title, Body, RecipientRoleCode in config).</summary>
    SendNotification,

    /// <summary>Creates a human work-item task linked to this instance (Title, AssignedRoleCode in config).</summary>
    CreateTask,

    /// <summary>Calls an external HTTP endpoint (Url, Method, Headers, BodyTemplate in config).</summary>
    CallWebhook,

    /// <summary>Publishes a named integration event on the message bus (EventType, Payload in config).</summary>
    PublishEvent,

    /// <summary>Starts a child workflow instance for a sub-process (TemplateEntityType in config).</summary>
    StartChildWorkflow,
}

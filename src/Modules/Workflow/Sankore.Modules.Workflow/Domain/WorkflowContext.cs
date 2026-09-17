namespace Sankore.Modules.Workflow.Domain;

/// <summary>
/// Runtime context passed to every <see cref="IActionExecutorDispatcher"/> execution.
/// Carries all information an action executor needs about the instance it is acting on.
/// </summary>
public sealed class WorkflowContext
{
    public Guid InstanceId { get; init; }
    public Guid TenantId { get; init; }
    public string EntityType { get; init; } = string.Empty;
    public Guid EntityId { get; init; }
    public Guid ActedByUserId { get; init; }

    /// <summary>Parsed key→value pairs from the instance's ContextJson snapshot.</summary>
    public IReadOnlyDictionary<string, object> Variables { get; init; } =
        new Dictionary<string, object>();
}

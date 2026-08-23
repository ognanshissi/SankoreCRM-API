using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.PublicApi;

/// <summary>
/// Public contract exposed by the Workflow module. Other modules reference only
/// this interface — never the main assembly or any of its internals.
/// </summary>
public interface IWorkflowModule
{
    /// <summary>
    /// Starts a workflow instance for the given entity, using the active template
    /// registered for <paramref name="request"/>.EntityType in the caller's tenant.
    /// Returns the new instance Id on success, or a failure result when no template
    /// is configured for that entity type.
    /// </summary>
    Task<Result<Guid>> StartWorkflowAsync(WorkflowStartRequest request, CancellationToken ct = default);

    /// <summary>
    /// Returns the current status of a workflow instance.
    /// Returns a failure result when the instance does not exist or belongs to a
    /// different tenant than <paramref name="tenantId"/>.
    /// </summary>
    Task<Result<WorkflowInstanceStatus>> GetInstanceStatusAsync(
        Guid instanceId, Guid tenantId, CancellationToken ct = default);
}

public sealed record WorkflowStartRequest(
    string EntityType,
    Guid EntityId,
    Guid StartedByUserId,
    Guid TenantId);

public sealed record WorkflowInstanceStatus(
    Guid InstanceId,
    string Status,
    int CurrentStepOrder,
    int TotalSteps,
    bool IsCompleted);

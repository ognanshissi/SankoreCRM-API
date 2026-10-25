namespace Sankore.Modules.Workflow.Infrastructure.Retention;

using Microsoft.Extensions.Logging;
using Sankore.Modules.Workflow.Domain;
using Sankore.Shared.Infrastructure.Retention;

/// <summary>The running circuits of a TEST institution, emptied (data-purge lot 4); the circuits' definitions stay.</summary>
internal sealed class WorkflowTenantDataWipe(WorkflowDbContext db, ILogger<WorkflowTenantDataWipe> logger) : ITenantDataWipe
{
    internal static readonly Type[] Keep =
    [
        typeof(WorkflowTemplate), typeof(WorkflowStepDefinition), typeof(WorkflowRule),
        typeof(WorkflowTransition), typeof(WorkflowAction), typeof(WorkflowTrigger),
    ];

    public string Module => "Workflow";

    public Task<int> WipeAsync(Guid tenantId, CancellationToken ct) => TenantRows.WipeAsync(db, tenantId, Keep, logger, ct);
}

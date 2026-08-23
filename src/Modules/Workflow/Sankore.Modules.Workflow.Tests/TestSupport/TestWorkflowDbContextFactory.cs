namespace Sankore.Modules.Workflow.Tests.TestSupport;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Creates <see cref="WorkflowDbContext"/> instances backed by the EF Core InMemory
/// provider. All contexts created from the same factory share one named database,
/// so seed data written through one context is visible to the next — identical to
/// the pattern used by Administration.Tests.
/// </summary>
public sealed class TestWorkflowDbContextFactory(Guid tenantId) : IDisposable
{
    private readonly string _databaseName = $"workflow-tests-{Guid.NewGuid()}";

    public WorkflowDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options;

        return new WorkflowDbContext(options, new FixedTenantContext(tenantId));
    }

    public void Dispose() { /* InMemory — nothing to dispose */ }
}

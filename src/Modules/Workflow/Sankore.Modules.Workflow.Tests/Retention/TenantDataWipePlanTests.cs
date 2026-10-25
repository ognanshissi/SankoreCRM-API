namespace Sankore.Modules.Workflow.Tests.Retention;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Modules.Workflow.Infrastructure.Retention;
using Sankore.Shared.Infrastructure.Retention;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Emptying a TEST institution (data-purge lot 4): the plan read from this module's REAL model —
/// no configuration table in it, every table with a statement, children before their parents.
/// With <c>TENANT_WIPE_SQL_OUT</c> set, the statements are also appended to that file, to be
/// replayed inside a rolled-back transaction against a real database.
/// </summary>
public sealed class TenantDataWipePlanTests
{
    [Fact]
    public void The_plan_keeps_the_configuration_and_deletes_children_first()
    {
        using var db = new WorkflowDbContext(
            new DbContextOptionsBuilder<WorkflowDbContext>()
                .UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none")
                .UseSnakeCaseNamingConvention()
                .Options,
            new FixedTenantContext(Guid.NewGuid()));

        TenantRows.Problems(db.Model, WorkflowTenantDataWipe.Keep).Should().BeEmpty();

        if (Environment.GetEnvironmentVariable("TENANT_WIPE_SQL_OUT") is { Length: > 0 } path)
            File.AppendAllLines(path, TenantRows.PlanFor(db.Model, WorkflowTenantDataWipe.Keep)
                .Where(t => t.Sql is not null).Select(t => t.Sql! + ";"));
    }
}

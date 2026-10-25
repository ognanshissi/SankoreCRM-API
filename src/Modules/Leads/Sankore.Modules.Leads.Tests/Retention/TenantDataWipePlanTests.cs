namespace Sankore.Modules.Leads.Tests.Retention;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Infrastructure.Retention;
using Sankore.Shared.Infrastructure.Retention;
using Sankore.Shared.Kernel;
using Sankore.Modules.Leads.Tests.TestSupport;
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
        using var db = new LeadsDbContext(
            new DbContextOptionsBuilder<LeadsDbContext>()
                .UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none")
                .UseSnakeCaseNamingConvention()
                .Options,
            new FixedTenantContext(Guid.NewGuid()), LeadsTestEncryptor.Instance);

        TenantRows.Problems(db.Model, LeadsTenantDataWipe.Keep).Should().BeEmpty();

        if (Environment.GetEnvironmentVariable("TENANT_WIPE_SQL_OUT") is { Length: > 0 } path)
            File.AppendAllLines(path, TenantRows.PlanFor(db.Model, LeadsTenantDataWipe.Keep)
                .Where(t => t.Sql is not null).Select(t => t.Sql! + ";"));
    }
}

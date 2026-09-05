using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Infrastructure;

/// <summary>
/// Used exclusively by EF Core design-time tools (dotnet ef migrations add …).
/// Never instantiated at runtime — the real WorkflowDbContext is resolved
/// from the DI container with the proper ITenantContext from the HTTP request.
/// </summary>
internal sealed class DesignTimeWorkflowDbContextFactory : IDesignTimeDbContextFactory<WorkflowDbContext>
{
    public WorkflowDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var opts = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(
                config.GetConnectionString("Database"),
                o => o.MigrationsHistoryTable("__EFMigrationsHistory", "workflow"))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new WorkflowDbContext(opts, new FixedTenantContext(Guid.Empty));
    }
}

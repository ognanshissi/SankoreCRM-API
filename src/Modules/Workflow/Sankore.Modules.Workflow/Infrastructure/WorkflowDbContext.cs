using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Shared.Infrastructure.Outbox;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Infrastructure;

public sealed class WorkflowDbContext(
    DbContextOptions<WorkflowDbContext> options,
    ITenantContext tenant) : DbContext(options)
{
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<WorkflowTemplate> WorkflowTemplates => Set<WorkflowTemplate>();
    public DbSet<WorkflowStepDefinition> WorkflowStepDefinitions => Set<WorkflowStepDefinition>();
    public DbSet<WorkflowInstance> WorkflowInstances => Set<WorkflowInstance>();
    public DbSet<WorkflowInstanceStep> WorkflowInstanceSteps => Set<WorkflowInstanceStep>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("workflow");
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WorkflowDbContext).Assembly);

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages");
            b.HasKey(m => m.Id);
            b.HasIndex(m => new { m.ProcessedAt, m.OccurredAt });
        });
        
        // Multi-tenant isolation — global query filters applied at ORM level.
        modelBuilder.Entity<WorkflowTemplate>()
            .HasQueryFilter(t => t.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<WorkflowInstance>()
            .HasQueryFilter(i => i.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<WorkflowInstanceStep>()
            .HasQueryFilter(s => s.TenantId == tenant.CurrentTenantId);
    }
}

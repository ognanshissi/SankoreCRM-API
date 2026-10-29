using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Domain.Events;
using Sankore.Modules.Workflow.Infrastructure.Notifications;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Outbox;
using Sankore.Shared.Kernel;
using Sankore.Shared.Infrastructure.Persistence;

namespace Sankore.Modules.Workflow.Infrastructure;

public sealed class WorkflowDbContext(
    DbContextOptions<WorkflowDbContext> options,
    ITenantContext tenant) : DbContext(options)
{
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<WorkflowTemplate> WorkflowTemplates => Set<WorkflowTemplate>();
    public DbSet<WorkflowStepDefinition> WorkflowStepDefinitions => Set<WorkflowStepDefinition>();
    public DbSet<WorkflowRule> WorkflowRules => Set<WorkflowRule>();
    public DbSet<WorkflowTransition> WorkflowTransitions => Set<WorkflowTransition>();
    public DbSet<WorkflowAction> WorkflowActions => Set<WorkflowAction>();
    public DbSet<WorkflowTask> WorkflowTasks => Set<WorkflowTask>();
    public DbSet<WorkflowAuditEntry> WorkflowAuditEntries => Set<WorkflowAuditEntry>();
    public DbSet<WorkflowTrigger> WorkflowTriggers => Set<WorkflowTrigger>();
    public DbSet<WorkflowInstance> WorkflowInstances => Set<WorkflowInstance>();
    public DbSet<WorkflowInstanceStep> WorkflowInstanceSteps => Set<WorkflowInstanceStep>();

    /// <summary>The domain events already written to the outbox by an earlier save of this context.</summary>
    private readonly HashSet<object> _announced = new(ReferenceEqualityComparer.Instance);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AnnounceNotificationSteps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AnnounceNotificationSteps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Writes a <see cref="WorkflowNotificationRequested"/> to the outbox for every notification
    /// step an instance has just franchie, in the transaction that records it. Here and not in the
    /// handlers because seven places move an instance (start, approval, task, SLA, child, trigger,
    /// facade) and a handler that forgot would silently send nothing — the very bug this replaces.
    /// </summary>
    private void AnnounceNotificationSteps()
    {
        var raised = ChangeTracker.Entries<WorkflowInstance>()
            .Where(e => e.State != EntityState.Deleted && e.State != EntityState.Detached)
            .SelectMany(e => e.Entity.DomainEvents.OfType<NotificationStepExecutedEvent>())
            .ToList();

        foreach (var executed in raised)
        {
            if (!_announced.Add(executed)) continue;

            var request = NotificationStepConfig.ToRequest(executed);
            OutboxMessages.Add(new OutboxMessage
            {
                Id = request.EventId,
                EventType = typeof(WorkflowNotificationRequested).AssemblyQualifiedName!,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(request, OutboxJson.Options),
                OccurredAt = request.OccurredAt,
            });
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
    }

    /// <summary>
    /// Every timestamp column stores UTC — Npgsql rejects any other offset outright.
    /// See <see cref="UtcTimestamps"/>.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        base.ConfigureConventions(builder);
        builder.UseUtcDateTimeOffsets();
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
        modelBuilder.Entity<WorkflowTask>()
            .HasQueryFilter(t => t.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<WorkflowTrigger>()
            .HasQueryFilter(t => t.TenantId == tenant.CurrentTenantId);
    }
}

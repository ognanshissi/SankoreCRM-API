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
        AnnounceManualTaskSteps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AnnounceNotificationSteps();
        AnnounceManualTaskSteps();
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

    /// <summary>The (step, opened?) pairs already written by an earlier save of this context.</summary>
    private readonly HashSet<(Guid StepId, bool Opened)> _taskAnnounced = [];

    /// <summary>
    /// A « Tâche manuelle » step that OPENS asks Leads for a CRM task (<see cref="WorkflowTaskRequested"/>);
    /// one that closes while still open — approved or rejected by hand, timed out, instance cancelled —
    /// withdraws it (<see cref="WorkflowTaskWithdrawn"/>). Read off the steps' status changes rather
    /// than raised by the domain, for the reason the notifications are announced here: many paths
    /// move an instance, and a task left in « Ma journée » for a closed step is work nobody needs.
    /// </summary>
    private void AnnounceManualTaskSteps()
    {
        var instances = ChangeTracker.Entries<WorkflowInstance>()
            .Where(e => e.State != EntityState.Deleted && e.State != EntityState.Detached)
            .ToDictionary(e => e.Entity.Id);

        // Materialised: adding outbox rows below changes the tracker being enumerated.
        foreach (var entry in ChangeTracker.Entries<WorkflowInstanceStep>()
                     .Where(e => e.Entity.Kind is StepKind.ManualTask or StepKind.Condition or StepKind.KycCheck or StepKind.IntegrationAction
                              && e.State is EntityState.Added or EntityState.Modified)
                     .ToList())
        {
            var step = entry.Entity;
            var wasOpen = entry.State == EntityState.Modified
                       && entry.Property(s => s.Status).OriginalValue == StepStatus.AwaitingApproval;
            var isOpen = step.Status == StepStatus.AwaitingApproval;
            instances.TryGetValue(step.InstanceId, out var instance);

            if (step.Kind == StepKind.IntegrationAction)
            {
                // Checked against what the back office recorded — it may already know the record.
                if (isOpen && !wasOpen && instance is not null && !IsTerminal(instance.Entity.Status)
                    && _taskAnnounced.Add((step.Id, true)))
                    AddToOutbox(new Gates.IntegrationStepReached(step.TenantId, step.InstanceId, step.Id));
                continue;
            }

            if (step.Kind == StepKind.KycCheck)
            {
                // Checked against the client's KYC at once — it may already be validated.
                if (isOpen && !wasOpen && instance is not null && !IsTerminal(instance.Entity.Status)
                    && _taskAnnounced.Add((step.Id, true)))
                    AddToOutbox(new Gates.KycCheckStepReached(step.TenantId, step.InstanceId, step.Id));
                continue;
            }

            if (step.Kind == StepKind.Condition)
            {
                // A condition is settled by its consumer on the record's current facts.
                if (isOpen && !wasOpen && instance is not null && !IsTerminal(instance.Entity.Status)
                    && _taskAnnounced.Add((step.Id, true)))
                    AddToOutbox(new Branching.ConditionStepReached(step.TenantId, step.InstanceId, step.Id));
                continue;
            }

            if (isOpen && !wasOpen && instance is not null && !IsTerminal(instance.Entity.Status))
            {
                if (_taskAnnounced.Add((step.Id, true)))
                    AddToOutbox(new WorkflowTaskRequested(
                        instance.Entity.TenantId, instance.Entity.Id, step.Id, step.Name,
                        instance.Entity.EntityType, instance.Entity.EntityId,
                        step.ApproverRoleCode, step.SlaHours));
            }
            else if (wasOpen && !isOpen && _taskAnnounced.Add((step.Id, false)))
            {
                AddToOutbox(new WorkflowTaskWithdrawn(step.TenantId, step.Id));
            }
        }

        // An instance cancelled or ended around a step that is still open: its task goes too.
        foreach (var (_, instance) in instances.Where(i => i.Value.State == EntityState.Modified
                     && IsTerminal(i.Value.Entity.Status)
                     && !IsTerminal(i.Value.Property(x => x.Status).OriginalValue)).ToList())
        {
            foreach (var step in instance.Entity.Steps.Where(s => s.Kind == StepKind.ManualTask
                                                               && s.Status == StepStatus.AwaitingApproval))
            {
                if (_taskAnnounced.Add((step.Id, false)))
                    AddToOutbox(new WorkflowTaskWithdrawn(step.TenantId, step.Id));
            }
        }
    }

    private static bool IsTerminal(WorkflowStatus status)
        => status is WorkflowStatus.Completed or WorkflowStatus.Rejected
                  or WorkflowStatus.Cancelled or WorkflowStatus.TimedOut;

    private void AddToOutbox<TEvent>(TEvent @event) where TEvent : IIntegrationEvent
        => OutboxMessages.Add(new OutboxMessage
        {
            Id = @event.EventId,
            EventType = typeof(TEvent).AssemblyQualifiedName!,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(@event, OutboxJson.Options),
            OccurredAt = @event.OccurredAt,
        });

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

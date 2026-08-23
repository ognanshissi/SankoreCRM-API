namespace Sankore.Modules.Notifications.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Notifications.Domain;
using Sankore.Shared.Kernel;

public sealed class NotificationsDbContext(
    DbContextOptions<NotificationsDbContext> options,
    ITenantContext tenant)
    : DbContext(options)
{
    public DbSet<EmailOutboxMessage> EmailOutboxMessages => Set<EmailOutboxMessage>();
    public DbSet<EmailTemplate> EmailTemplates => Set<EmailTemplate>();
    public DbSet<EmailDeliveryLog> EmailDeliveryLogs => Set<EmailDeliveryLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("notifications");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NotificationsDbContext).Assembly);

        modelBuilder.Entity<EmailOutboxMessage>()
            .HasQueryFilter(m => m.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<EmailDeliveryLog>()
            .HasQueryFilter(l => l.TenantId == tenant.CurrentTenantId);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
    }
}

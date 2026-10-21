namespace Sankore.Modules.Notifications.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Notifications.Domain;

internal sealed class UserNotificationConfiguration : IEntityTypeConfiguration<UserNotification>
{
    public void Configure(EntityTypeBuilder<UserNotification> b)
    {
        b.ToTable("user_notifications");
        b.HasKey(n => n.Id);
        b.Property(n => n.Id).ValueGeneratedNever();

        b.Property(n => n.Category).HasMaxLength(60).IsRequired();
        b.Property(n => n.Title).HasMaxLength(UserNotification.TitleMaxLength).IsRequired();
        b.Property(n => n.Body).HasMaxLength(UserNotification.BodyMaxLength);
        b.Property(n => n.Link).HasMaxLength(UserNotification.LinkMaxLength);
        b.Property(n => n.IdempotencyKey).HasMaxLength(200).IsRequired();

        // Une même clé ne notifie qu'une fois : un consommateur rejoué ne fait pas sonner deux fois.
        b.HasIndex(n => new { n.TenantId, n.UserId, n.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("ux_user_notifications_idempotency");

        // La cloche : les plus récentes d'un utilisateur, et le compteur des non lues.
        b.HasIndex(n => new { n.TenantId, n.UserId, n.CreatedAt });
        b.HasIndex(n => new { n.TenantId, n.UserId, n.ReadAt });
    }
}

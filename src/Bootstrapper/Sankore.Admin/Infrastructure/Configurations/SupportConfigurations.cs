using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Admin.Domain;

namespace Sankore.Admin.Infrastructure.Configurations;

public sealed class SupportTicketConfiguration : IEntityTypeConfiguration<SupportTicket>
{
    public void Configure(EntityTypeBuilder<SupportTicket> builder)
    {
        builder.ToTable("SupportTickets");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Ignore(t => t.Reference);

        // The reference a tenant quotes on the phone: one sequence for the whole platform.
        builder.Property(t => t.Number).UseIdentityByDefaultColumn();
        builder.HasIndex(t => t.Number).IsUnique().HasDatabaseName("ux_support_tickets_number");

        builder.Property(t => t.Subject).HasMaxLength(SupportTicket.SubjectMaxLength).IsRequired();
        builder.Property(t => t.RequesterEmail).HasMaxLength(SupportTicket.EmailMaxLength).IsRequired();
        builder.Property(t => t.RequesterName).HasMaxLength(SupportTicket.NameMaxLength).IsRequired();
        builder.Property(t => t.RequesterRole).HasMaxLength(SupportTicket.RoleMaxLength);
        builder.Property(t => t.PageUrl).HasMaxLength(SupportTicket.PageUrlMaxLength);
        builder.Property(t => t.AppVersion).HasMaxLength(SupportTicket.AppVersionMaxLength);
        builder.Property(t => t.UserAgent).HasMaxLength(SupportTicket.UserAgentMaxLength);

        builder.Property(t => t.Status).IsRequired().HasMaxLength(20)
            .HasConversion(v => v.ToString(), v => Enum.Parse<SupportTicketStatus>(v));
        builder.Property(t => t.Category).IsRequired().HasMaxLength(20)
            .HasConversion(v => v.ToString(), v => Enum.Parse<SupportTicketCategory>(v));
        builder.Property(t => t.Priority).IsRequired().HasMaxLength(20)
            .HasConversion(v => v.ToString(), v => Enum.Parse<SupportTicketPriority>(v));

        builder.HasMany(t => t.Messages).WithOne().HasForeignKey(m => m.TicketId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(t => t.Messages).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Logical reference only — no physical FK, like TenantDomains.
        builder.HasIndex(t => new { t.TenantId, t.LastActivityAt })
            .HasDatabaseName("ix_support_tickets_tenant_activity");
        builder.HasIndex(t => new { t.Status, t.LastActivityAt })
            .HasDatabaseName("ix_support_tickets_status_activity");
        builder.HasIndex(t => t.AssignedAdminId)
            .HasDatabaseName("ix_support_tickets_assigned_admin");
    }
}

public sealed class SupportMessageConfiguration : IEntityTypeConfiguration<SupportMessage>
{
    public void Configure(EntityTypeBuilder<SupportMessage> builder)
    {
        builder.ToTable("SupportMessages");
        builder.HasKey(m => m.Id);

        // Appended to a LOADED ticket: without it EF tracks the new row as Modified and updates
        // a row that was never inserted.
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Ignore(m => m.IsVisibleToTenant);

        builder.Property(m => m.AuthorName).HasMaxLength(SupportTicket.NameMaxLength).IsRequired();
        builder.Property(m => m.Body).HasMaxLength(SupportTicket.BodyMaxLength);

        builder.Property(m => m.Kind).IsRequired().HasMaxLength(20)
            .HasConversion(v => v.ToString(), v => Enum.Parse<SupportMessageKind>(v));
        builder.Property(m => m.AuthorKind).IsRequired().HasMaxLength(20)
            .HasConversion(v => v.ToString(), v => Enum.Parse<SupportAuthorKind>(v));
        builder.Property(m => m.Status).HasMaxLength(20)
            .HasConversion(v => v!.Value.ToString(), v => Enum.Parse<SupportTicketStatus>(v));

        builder.HasIndex(m => new { m.TicketId, m.CreatedAt })
            .HasDatabaseName("ix_support_messages_ticket_created");

        // The feed Sankore.Api reads to tell requesters the platform answered.
        builder.HasIndex(m => new { m.TenantId, m.CreatedAt })
            .HasDatabaseName("ix_support_messages_tenant_created");
    }
}

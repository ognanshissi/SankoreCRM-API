using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

internal sealed class LeadReminderConfiguration : IEntityTypeConfiguration<LeadReminder>
{
    public void Configure(EntityTypeBuilder<LeadReminder> builder)
    {
        builder.ToTable("lead_reminders");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Title).HasMaxLength(200).IsRequired();
        builder.Property(r => r.Notes).HasMaxLength(1000);

        builder.Property(r => r.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.HasOne<Lead>()
            .WithMany()
            .HasForeignKey(r => r.LeadId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => new { r.LeadId, r.Status });
        builder.HasIndex(r => new { r.TenantId, r.DueAt, r.Status });
    }
}

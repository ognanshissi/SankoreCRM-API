using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

internal sealed class LeadOwnerAssignmentHistoryConfiguration
    : IEntityTypeConfiguration<LeadOwnerAssignmentHistory>
{
    public void Configure(EntityTypeBuilder<LeadOwnerAssignmentHistory> builder)
    {
        builder.ToTable("lead_owner_assignment_histories");

        builder.HasKey(h => h.Id);

        builder.Property(h => h.AssignmentMethod).HasMaxLength(50).IsRequired();
        builder.Property(h => h.Reason).HasMaxLength(500);

        builder.HasOne<Lead>()
            .WithMany()
            .HasForeignKey(h => h.LeadId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(h => new { h.LeadId, h.AssignedAt });
        builder.HasIndex(h => new { h.TenantId, h.NewOwnerId });
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

internal sealed class NurturingEnrollmentConfiguration : IEntityTypeConfiguration<NurturingEnrollment>
{
    public void Configure(EntityTypeBuilder<NurturingEnrollment> builder)
    {
        builder.ToTable("nurturing_enrollments");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Status)
            .HasConversion<string>()
            .HasMaxLength(20);
        builder.Property(e => e.CancellationReason).HasMaxLength(500);
        builder.HasIndex(e => new { e.TenantId, e.Status, e.NextStepDueAt });
        builder.HasIndex(e => new { e.LeadId, e.Status });
    }
}

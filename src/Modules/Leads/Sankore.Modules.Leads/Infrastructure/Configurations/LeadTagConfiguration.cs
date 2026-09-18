using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

internal sealed class LeadTagConfiguration : IEntityTypeConfiguration<LeadTag>
{
    public void Configure(EntityTypeBuilder<LeadTag> builder)
    {
        builder.ToTable("lead_tags");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Tag).HasMaxLength(50).IsRequired();

        builder.HasOne<Lead>()
            .WithMany()
            .HasForeignKey(t => t.LeadId)
            .OnDelete(DeleteBehavior.Cascade);

        // Prevent duplicate tags on the same lead.
        builder.HasIndex(t => new { t.LeadId, t.Tag }).IsUnique();
        builder.HasIndex(t => new { t.TenantId, t.Tag });
    }
}

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

internal sealed class DuplicateDismissalConfiguration : IEntityTypeConfiguration<DuplicateDismissal>
{
    public void Configure(EntityTypeBuilder<DuplicateDismissal> builder)
    {
        builder.ToTable("duplicate_dismissals");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Reason).HasMaxLength(500);

        // Prevent double-recording the same agent decision.
        // The pair is stored in one direction (LeadId, CandidateLeadId);
        // queries check both directions for bidirectional suppression.
        builder.HasIndex(d => new { d.TenantId, d.LeadId, d.CandidateLeadId })
               .IsUnique();
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

internal sealed class NurturingStepConfiguration : IEntityTypeConfiguration<NurturingStep>
{
    public void Configure(EntityTypeBuilder<NurturingStep> builder)
    {
        builder.ToTable("nurturing_steps");
        builder.HasKey(st => st.Id);
        builder.Property(st => st.EmailTemplateKey).HasMaxLength(100).IsRequired();
        builder.Property(st => st.Subject).HasMaxLength(200);
        builder.HasIndex(st => new { st.SequenceId, st.Order });
    }
}

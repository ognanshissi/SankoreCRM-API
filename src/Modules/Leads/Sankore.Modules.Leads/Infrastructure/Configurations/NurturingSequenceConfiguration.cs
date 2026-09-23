using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

internal sealed class NurturingSequenceConfiguration : IEntityTypeConfiguration<NurturingSequence>
{
    public void Configure(EntityTypeBuilder<NurturingSequence> builder)
    {
        builder.ToTable("nurturing_sequences");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Name).HasMaxLength(200).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(2000);
        builder.HasMany(s => s.Steps).WithOne().HasForeignKey(st => st.SequenceId).OnDelete(DeleteBehavior.Cascade);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

internal sealed class ScoreHistoryConfiguration : IEntityTypeConfiguration<ScoreHistory>
{
    public void Configure(EntityTypeBuilder<ScoreHistory> builder)
    {
        builder.ToTable("score_histories");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.TriggerEvent).HasMaxLength(100).IsRequired();
        builder.Property(s => s.FactorsJson).HasColumnType("jsonb").IsRequired();

        builder.HasOne<Lead>()
            .WithMany()
            .HasForeignKey(s => s.LeadId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => new { s.LeadId, s.RecalculatedAt });
    }
}

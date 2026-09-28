using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class DuplicateCandidateConfiguration : IEntityTypeConfiguration<DuplicateCandidate>
{
    public void Configure(EntityTypeBuilder<DuplicateCandidate> builder)
    {
        builder.ToTable("duplicate_candidates");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.TenantId).IsRequired();
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(d => d.ReasonsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(d => d.FingerprintA).HasMaxLength(64).IsRequired();
        builder.Property(d => d.FingerprintB).HasMaxLength(64).IsRequired();

        // The pair is always stored with ClientAId < ClientBId (domain invariant),
        // so one unique index is enough to make the pair canonical: the same two
        // clients can never yield two candidate rows.
        builder.HasIndex(d => new { d.TenantId, d.ClientAId, d.ClientBId })
            .IsUnique()
            .HasDatabaseName("ux_duplicate_candidates_pair");

        builder.HasIndex(d => new { d.TenantId, d.Status });

        builder.HasOne<Client>()
            .WithMany()
            .HasForeignKey(d => d.ClientAId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Client>()
            .WithMany()
            .HasForeignKey(d => d.ClientBId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

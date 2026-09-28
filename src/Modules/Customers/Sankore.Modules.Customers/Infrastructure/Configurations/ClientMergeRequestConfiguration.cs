using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class ClientMergeRequestConfiguration : IEntityTypeConfiguration<ClientMergeRequest>
{
    public void Configure(EntityTypeBuilder<ClientMergeRequest> builder)
    {
        builder.ToTable("client_merge_requests");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        // Per-field survivor/absorbed choices made by the requester, replayed at
        // execution time — jsonb so a new mergeable field needs no migration.
        builder.Property(r => r.FieldChoicesJson).HasColumnType("jsonb").IsRequired();
        builder.Property(r => r.DecisionComment).HasMaxLength(1000);

        builder.HasIndex(r => new { r.TenantId, r.Status });
        // WorkflowInstanceId points into the workflow module: an opaque reference,
        // indexed so the approval callback can find its request in one hit.
        builder.HasIndex(r => new { r.TenantId, r.WorkflowInstanceId });

        builder.HasOne<Client>()
            .WithMany()
            .HasForeignKey(r => r.SurvivorClientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Client>()
            .WithMany()
            .HasForeignKey(r => r.AbsorbedClientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Ignore(r => r.DomainEvents);
    }
}

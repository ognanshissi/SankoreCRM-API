using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class ClientComplaintConfiguration : IEntityTypeConfiguration<ClientComplaint>
{
    public void Configure(EntityTypeBuilder<ClientComplaint> builder)
    {
        builder.ToTable("client_complaints");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.TenantId).IsRequired();
        builder.Property(c => c.Reference).HasMaxLength(30).IsRequired();
        builder.Property(c => c.Channel).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(c => c.Category).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(c => c.Subject).HasMaxLength(ClientComplaint.SubjectMaxLength).IsRequired();
        // Ciphertext grows with the text: no cap.
        builder.Property(c => c.BodyEncrypted).HasColumnType("text");
        builder.Property(c => c.ResponseEncrypted).HasColumnType("text");
        builder.Property(c => c.ClosureReason).HasMaxLength(ClientComplaint.ReasonMaxLength);

        // The reference is what the client quotes back: one per tenant. Also what makes two
        // concurrent recordings that computed the same next number fail instead of colliding.
        builder.HasIndex(c => new { c.TenantId, c.Reference }).IsUnique();
        builder.HasIndex(c => new { c.TenantId, c.ClientId });
        builder.HasIndex(c => new { c.TenantId, c.Status, c.ResolutionDueAt });
    }
}

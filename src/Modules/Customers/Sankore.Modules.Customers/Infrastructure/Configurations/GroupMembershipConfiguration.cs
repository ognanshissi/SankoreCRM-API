using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class GroupMembershipConfiguration : IEntityTypeConfiguration<GroupMembership>
{
    public void Configure(EntityTypeBuilder<GroupMembership> builder)
    {
        builder.ToTable("group_memberships");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.TenantId).IsRequired();
        builder.Property(m => m.OfficeRole).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(m => m.LeaveReason).HasMaxLength(1000);

        builder.HasIndex(m => new { m.TenantId, m.ClientId });
        builder.HasIndex(m => new { m.TenantId, m.GroupId });

        // A client can only be a live member of a group once, but may rejoin after
        // leaving — hence the partial index on the open rows only.
        builder.HasIndex(m => new { m.TenantId, m.GroupId, m.ClientId })
            .IsUnique()
            .HasFilter("left_at IS NULL")
            .HasDatabaseName("ux_group_memberships_active");

        builder.HasOne<Client>()
            .WithMany()
            .HasForeignKey(m => m.ClientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Ignore(m => m.IsActive);
    }
}

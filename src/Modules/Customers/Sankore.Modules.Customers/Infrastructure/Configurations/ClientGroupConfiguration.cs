using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class ClientGroupConfiguration : IEntityTypeConfiguration<ClientGroup>
{
    public void Configure(EntityTypeBuilder<ClientGroup> builder)
    {
        builder.ToTable("client_groups");
        builder.HasKey(g => g.Id);

        builder.Property(g => g.TenantId).IsRequired();
        builder.Property(g => g.Type).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(g => g.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(g => g.Name).HasMaxLength(150).IsRequired();
        builder.Property(g => g.DissolutionReason).HasMaxLength(1000);

        // Group names must be unambiguous within an agency (GROUP_NAME_ALREADY_USED).
        builder.HasIndex(g => new { g.TenantId, g.AgencyId, g.Name })
            .IsUnique()
            .HasDatabaseName("ux_client_groups_name");

        builder.Property(g => g.Version)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasMany(g => g.Memberships)
            .WithOne()
            .HasForeignKey(m => m.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
        var memberships = builder.Navigation(g => g.Memberships);
        memberships.HasField("_memberships");
        memberships.Metadata.SetPropertyAccessMode(PropertyAccessMode.Field);

        // AgencyId belongs to the administration schema: no physical FK.
        builder.Ignore(g => g.ActiveMemberCount);
        builder.Ignore(g => g.DomainEvents);
    }
}

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

internal sealed class ClientProfileChangeNoticeConfiguration
    : IEntityTypeConfiguration<ClientProfileChangeNotice>
{
    public void Configure(EntityTypeBuilder<ClientProfileChangeNotice> b)
    {
        b.ToTable("client_profile_change_notices");
        b.HasKey(n => n.Id);

        b.Property(n => n.TenantId).IsRequired();
        b.Property(n => n.ClientId).IsRequired();

        // Enum as a string, the repo convention: a reordered enum must not silently re-label
        // every stored row.
        b.Property(n => n.Kind).HasConversion<string>().HasMaxLength(30).IsRequired();

        b.Property(n => n.NoticedOn).IsRequired();
        b.Property(n => n.Note).HasMaxLength(500);
        b.Property(n => n.DismissedReason).HasMaxLength(500);

        b.Ignore(n => n.IsOpen);

        // The report's own query: the OPEN notices of a tenant, oldest first. Filtered, so the
        // index covers exactly the rows a compliance screen reads and not the closed history.
        b.HasIndex(n => new { n.TenantId, n.NoticedOn })
            .HasFilter("integrated_on IS NULL AND dismissed_reason IS NULL")
            .HasDatabaseName("ix_profile_notices_open");

        // A client's own history, which the 360 view shows.
        b.HasIndex(n => new { n.TenantId, n.ClientId })
            .HasDatabaseName("ix_profile_notices_client");
    }
}

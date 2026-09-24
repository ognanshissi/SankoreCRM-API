namespace Sankore.Modules.Leads.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

internal sealed class SdkVersionConfiguration : IEntityTypeConfiguration<SdkVersion>
{
    public void Configure(EntityTypeBuilder<SdkVersion> b)
    {
        b.ToTable("sdk_versions");
        b.HasKey(v => v.Id);
        b.Property(v => v.Version).HasMaxLength(20).IsRequired();
        b.Property(v => v.SriHash).HasMaxLength(200).IsRequired();
        b.Property(v => v.FileName).HasMaxLength(50).IsRequired();

        b.HasIndex(v => v.Version).IsUnique().HasDatabaseName("ux_sdk_version");
        b.HasIndex(v => new { v.Major, v.IsCurrent })
            .HasFilter("is_current = true")
            .HasDatabaseName("ix_sdk_major_current");
    }
}

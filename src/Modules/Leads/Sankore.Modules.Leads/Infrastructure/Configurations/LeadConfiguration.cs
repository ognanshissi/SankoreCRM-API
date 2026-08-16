using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

internal sealed class LeadConfiguration: IEntityTypeConfiguration<Lead>
{
    public void Configure(EntityTypeBuilder<Lead> builder)
    {
        builder.ToTable("leads");
        
        builder.HasKey(l => l.Id);
        builder.Property(l => l.FullName).HasMaxLength(200).IsRequired();
        builder.Property(l => l.PhoneNumber).HasMaxLength(30).IsRequired();
        builder.Property(l => l.InterestedProduct).HasMaxLength(100).IsRequired();
        builder.Property(l => l.PreferredLanguage).HasMaxLength(50).IsRequired();
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(l => l.Source).HasConversion<string>().HasMaxLength(30);

        builder.OwnsOne(l => l.Location, loc =>
        {
            loc.Property(p => p.Latitude).HasColumnName("lat");
            loc.Property(p => p.Longitude).HasColumnName("lng");
        });

        builder.HasIndex(l => new { l.TenantId, l.Status });
        builder.HasIndex(l => l.PhoneNumber);
        builder.HasIndex(l => l.FullName);

        // Domain events are transient, never persisted as a column.
        builder.Ignore(l => l.DomainEvents);
    }
}
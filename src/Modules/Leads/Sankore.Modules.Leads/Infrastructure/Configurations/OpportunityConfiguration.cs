using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

internal sealed class OpportunityConfiguration : IEntityTypeConfiguration<Opportunity>
{
    public void Configure(EntityTypeBuilder<Opportunity> builder)
    {
        builder.ToTable("opportunities");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Title).HasMaxLength(200).IsRequired();
        builder.Property(o => o.Description).HasMaxLength(2000);
        builder.Property(o => o.Product).HasMaxLength(100).IsRequired();
        builder.Property(o => o.CustomerEntityType).HasMaxLength(50);
        builder.Property(o => o.CloseReason).HasMaxLength(500);
        builder.Property(o => o.Stage)
            .HasConversion<string>()
            .HasMaxLength(30);
        builder.OwnsOne(o => o.EstimatedAmount, m =>
        {
            m.Property(p => p.Amount).HasColumnName("estimated_amount").HasPrecision(18, 4);
            m.Property(p => p.Currency).HasColumnName("estimated_currency").HasMaxLength(3);
        });
        builder.HasIndex(o => new { o.TenantId, o.LeadId });
        builder.HasIndex(o => new { o.TenantId, o.CustomerEntityId })
            .HasFilter("customer_entity_id IS NOT NULL");
        builder.HasIndex(o => new { o.TenantId, o.Stage });
        builder.Ignore(o => o.DomainEvents);
    }
}

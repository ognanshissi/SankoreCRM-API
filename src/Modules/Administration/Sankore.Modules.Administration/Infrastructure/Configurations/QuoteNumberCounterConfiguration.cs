using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

/// <summary>
/// Quote lines, lot 2. The table name and its (tenant_id, year) key are written by hand in
/// <c>QuoteIssuerReader.TakeAsync</c>'s upsert: rename one, rename the other.
/// </summary>
public class QuoteNumberCounterConfiguration : IEntityTypeConfiguration<QuoteNumberCounter>
{
    public void Configure(EntityTypeBuilder<QuoteNumberCounter> builder)
    {
        builder.ToTable("quote_number_counters");
        builder.HasKey(x => new { x.TenantId, x.Year });
    }
}

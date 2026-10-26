namespace Sankore.Modules.Customers.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

internal sealed class ProductHoldingConfiguration : IEntityTypeConfiguration<ProductHolding>
{
    /// <summary>
    /// At most one LIVE holding of a product line per client, when the line forbids multiple
    /// holdings (BNF-B-22) — "une seule police santé par personne".
    ///
    /// <para>
    /// The rule lives in M12's catalogue and a partial index can only see this table, so the
    /// holding carries it as a snapshot (<see cref="ProductHolding.SinglePerLine"/>) and the filter
    /// reads that. The handler still checks first, for the error code an agent can act on; the
    /// index covers the two requests arriving at the same instant. Without the snapshot the index
    /// forbade EVERY second live contract — a won project on « Obsèques Sérénité », a line that
    /// allows several, ended in a unique violation and no draft.
    /// </para>
    ///
    /// <para>
    /// Rows written before the column default to false: no migration reads another module's
    /// schema, so they are not covered by the index, only by the handler's check.
    /// </para>
    /// </summary>
    internal const string OneLiveHoldingIndex = "ux_product_holdings_live_per_line";

    public void Configure(EntityTypeBuilder<ProductHolding> builder)
    {
        builder.ToTable("product_holdings");
        builder.HasKey(h => h.Id);

        builder.Property(h => h.TenantId).IsRequired();

        // A SNAPSHOT of the line at subscription, not a lookup — see the entity.
        builder.Property(h => h.ProductCode).HasMaxLength(50).IsRequired();
        builder.Property(h => h.ProductCategory).HasMaxLength(30).IsRequired();

        builder.Property(h => h.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(h => h.Origin).HasConversion<string>().HasMaxLength(30).IsRequired();

        builder.Property(h => h.SignatureReference).HasMaxLength(500);
        builder.Property(h => h.ExternalRef).HasMaxLength(100);
        builder.Property(h => h.ExternalSystem).HasMaxLength(50);
        builder.Property(h => h.RejectionReason).HasMaxLength(1000);
        builder.Property(h => h.CloseReason).HasMaxLength(500);

        builder.Property(h => h.ClauseVersion).HasDefaultValue(1).IsRequired();

        // Typed jsonb, with the default EF cannot generate correctly on its own: a non-nullable
        // converted property gets `DEFAULT ''`, which PostgreSQL rejects on a jsonb column. `{}`
        // deserialises to HoldingSpecifics.None because every member is optional.
        builder.Property(h => h.Specifics)
            .HasColumnType("jsonb")
            .HasColumnName("specifics")
            .HasConversion(new HoldingSpecificsConverter())
            .HasDefaultValueSql("'{}'::jsonb");

        // xmin, the module's concurrency convention.
        builder.Property(h => h.Version)
            .IsRowVersion()
            .HasColumnName("xmin")
            .HasColumnType("xid");

        // The client is in THIS schema, so a foreign key is legitimate. Restrict and not Cascade:
        // a holding is the record of a contract, and it must not disappear as a side effect.
        builder.HasOne<Client>()
            .WithMany()
            .HasForeignKey(h => h.ClientId)
            .OnDelete(DeleteBehavior.Restrict);

        // ProductLineId carries no FK — another schema — so a retired line leaves a dangling id
        // and every reader degrades. Same shape as Lead.ProductLineId.

        builder.HasMany(h => h.Designations)
            .WithOne()
            .HasForeignKey(d => d.HoldingId)
            // Cascade, unlike the client FK above: a designation has no meaning without its
            // holding, and this covers a holding genuinely being deleted — never the ordinary
            // path, which is Close() plus ValidTo.
            .OnDelete(DeleteBehavior.Cascade);

        // Serves the client's product list, the screen this table exists for.
        builder.HasIndex(h => new { h.TenantId, h.ClientId, h.Status });
        builder.HasIndex(h => new { h.TenantId, h.ProductLineId });

        // Serves BNF-B-16's outbound queue and BNF-B-17's reconciliation. Partial: the states that
        // need attention are a small minority of a table that only grows.
        builder.HasIndex(h => new { h.TenantId, h.Status })
               .HasFilter("status IN ('PendingSync', 'Rejected')")
               .HasDatabaseName("ix_product_holdings_pending");

        builder.HasIndex(h => new { h.TenantId, h.ExternalRef })
               .HasFilter("external_ref IS NOT NULL");

        builder.HasIndex(h => new { h.TenantId, h.ClientId, h.ProductLineId })
               .IsUnique()
               .HasDatabaseName(OneLiveHoldingIndex)
               .HasFilter(
                   "status IN ('Draft', 'PendingSignature', 'PendingSync', 'Active') "
                   + "AND origin = 'CrmSubscription' AND single_per_line");

        builder.Ignore(h => h.IsReadOnly);
        builder.Ignore(h => h.IsEditable);
        builder.Ignore(h => h.CurrentDesignations);
        builder.Ignore(h => h.DomainEvents);
    }
}

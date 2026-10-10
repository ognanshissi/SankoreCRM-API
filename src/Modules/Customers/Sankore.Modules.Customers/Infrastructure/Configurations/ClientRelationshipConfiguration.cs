using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Persistence;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class ClientRelationshipConfiguration : IEntityTypeConfiguration<ClientRelationship>
{
    /// <summary>
    /// At most ONE ACTIVE spouse per client (BNF-B-13) — the database half of an invariant that
    /// cannot be held by a read. The handler's check gives an actionable error code; this is what
    /// holds when two requests record a spouse at the same instant.
    ///
    /// <para>
    /// It is added in a SECOND migration, after a detection query has been run against the
    /// existing rows: <c>client_relationships</c> predates the invariant and has never enforced
    /// it, so creating the index blind would fail with <c>23505</c> on any tenant that recorded two
    /// spouses. See the migration's own comment.
    /// </para>
    /// </summary>
    internal const string OneSpouseIndex = "uq_client_one_spouse";

    public void Configure(EntityTypeBuilder<ClientRelationship> builder)
    {
        builder.ToTable("client_relationships", t =>
        {
            // The pairing of type and union type, as a CHECK. The factory refuses both halves
            // already; this catches a row arriving any other way — an import, a manual fix.
            //
            // Note the NULL-safety: `type = 'Spouse'` is unknown for no row (type is NOT NULL), so
            // the equality on the right has to be written with IS NOT NULL rather than as a second
            // comparison, or a null union type would make the whole predicate unknown and pass.
            t.HasCheckConstraint(
                "ck_client_relationship_union",
                "(type = 'Spouse') OR (spouse_union_type IS NULL)");
        });
        builder.HasKey(r => r.Id);

        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.Type).HasConversion<string>().HasMaxLength(30).IsRequired();

        builder.Property(r => r.ExternalFullName).HasMaxLength(150);
        builder.Property(r => r.ExternalPhoneEncrypted).HasMaxLength(2000);
        builder.Property(r => r.ExternalPhoneBlindIndex).HasMaxLength(64);
        builder.Property(r => r.EncryptedExternalDocumentNumber).HasMaxLength(2000);
        builder.Property(r => r.CloseReason).HasMaxLength(1000);

        builder.HasIndex(r => new { r.TenantId, r.ClientId });
        builder.HasIndex(r => new { r.TenantId, r.RelatedClientId });
        builder.HasIndex(r => new { r.TenantId, r.ClientId, r.Type });

        // ── Household (BNF-B-13) ────────────────────────────────────────────
        builder.Property(r => r.SpouseUnionType).HasConversion<string>().HasMaxLength(15);
        builder.Property(r => r.UsesPrincipalAddress).HasDefaultValue(false).IsRequired();

        builder.Property(r => r.ExternalFirstName).HasMaxLength(100);
        builder.Property(r => r.ExternalLastName).HasMaxLength(100);
        builder.Property(r => r.ExternalUsageName).HasMaxLength(100);
        builder.Property(r => r.ExternalGender).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.ExternalPostalCode).HasMaxLength(20);
        builder.Property(r => r.ExternalCity).HasMaxLength(100);
        builder.Property(r => r.ExternalCountryCode).HasMaxLength(2);

        // Encrypted by the handler, like the two columns above it on this table — uncapped,
        // because a ~200-character address becomes roughly 300 characters of payload and a cap
        // would truncate ciphertext into something that no longer decrypts.
        builder.Property(r => r.ExternalAddressEncrypted).HasMaxLength(2000);

        // PersonIdentity is NOT owned here (see the entity); PepStatus is, through the shared
        // mapping, so a conversion snapshot lines up field for field with the lead side.
        builder.OwnsPepStatus(r => r.Pep);

        // SourceLeadMemberId needs no .Property(): a plain Guid? becomes a nullable uuid under the
        // module's snake-case convention. It carries NO foreign key — another schema.

        // The NAMED overload, and it is load-bearing: EF identifies an index by its property set,
        // so declaring a second one over (tenant_id, client_id) through the usual HasIndex REPLACES
        // the plain index above — which serves every "list this client's relationships" read. The
        // name makes them two distinct indexes instead of one redefinition.
        builder.HasIndex(r => new { r.TenantId, r.ClientId }, OneSpouseIndex)
               // The name above is the MODEL-level one, which is only what lets two indexes share
               // a property set; the database name is a separate call, and without it the column
               // ends up as ix_client_relationships_tenant_id_client_id1.
               .HasDatabaseName(OneSpouseIndex)
               .IsUnique()
               .HasFilter("type = 'Spouse' AND valid_to IS NULL");

        // Both ends live in customers.clients, so FKs are legitimate. Restrict
        // rather than Cascade: relationships are compliance evidence and must
        // never disappear as a side effect of deleting the other party.
        builder.HasOne<Client>()
            .WithMany()
            .HasForeignKey(r => r.ClientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Client>()
            .WithMany()
            .HasForeignKey(r => r.RelatedClientId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Ignore(r => r.IsActive);
        builder.Ignore(r => r.IsHouseholdMember);
        builder.Ignore(r => r.IsClientLinked);
    }
}

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

internal sealed class BeneficiaryDesignationConfiguration
    : IEntityTypeConfiguration<BeneficiaryDesignation>
{
    public void Configure(EntityTypeBuilder<BeneficiaryDesignation> builder)
    {
        builder.ToTable("beneficiary_designations", t =>
        {
            // An external person has no id and must have a name; anybody else is the reverse. The
            // aggregate refuses both halves already — this catches a row arriving any other way.
            t.HasCheckConstraint(
                "ck_designation_subject",
                "(subject_type = 'ExternalPerson' AND subject_id IS NULL AND external_full_name IS NOT NULL) "
                + "OR (subject_type <> 'ExternalPerson' AND subject_id IS NOT NULL)");

            t.HasCheckConstraint("ck_designation_share", "share_percent IS NULL OR (share_percent >= 0 AND share_percent <= 100)");
            t.HasCheckConstraint("ck_designation_rank", "rank >= 1");
        });

        builder.HasKey(d => d.Id);

        // The application assigns the key in BeneficiaryDesignation.Create, so EF has to be told —
        // otherwise a designation appended to a LOADED holding is tracked Modified rather than
        // Added and SaveChanges tries to UPDATE a row that was never inserted. Exactly the trap
        // LeadHouseholdMemberConfiguration documents; tests that add children to a NEW parent do
        // not catch it, and every endpoint here loads then modifies.
        builder.Property(d => d.Id).ValueGeneratedNever();

        builder.Property(d => d.TenantId).IsRequired();
        builder.Property(d => d.RoleCode).HasMaxLength(50).IsRequired();
        builder.Property(d => d.SubjectType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(d => d.StatusReason).HasMaxLength(500);
        builder.Property(d => d.ExternalFullName).HasMaxLength(150);
        builder.Property(d => d.ExternalRelationship).HasMaxLength(100);

        // Two decimals, because thirds of a capital are 33.33 / 33.33 / 33.34 and the clause has to
        // total exactly 100 — a lower precision would make that arithmetic impossible to express.
        builder.Property(d => d.SharePercent).HasPrecision(5, 2);

        builder.Property(d => d.Rank).HasDefaultValue(1).IsRequired();
        builder.Property(d => d.ClauseVersion).HasDefaultValue(1).IsRequired();

        builder.HasIndex(d => new { d.TenantId, d.HoldingId });

        // Serves the clause-as-it-stands read, which is every screen. Partial: superseded versions
        // accumulate and no screen wants them.
        builder.HasIndex(d => new { d.TenantId, d.HoldingId, d.RoleCode })
               .HasFilter("valid_to IS NULL")
               .HasDatabaseName("ix_designations_current");

        // Serves "this household member left — what did they hold" (BNF-B-13), across holdings.
        builder.HasIndex(d => new { d.TenantId, d.SubjectId })
               .HasFilter("subject_id IS NOT NULL AND valid_to IS NULL");

        // NO unique index on (holding, role, rank, subject): the same person may be designated on
        // several holdings and, within one holding, the versions of a clause deliberately repeat
        // the same subject. The duplicate rule is per SUBMISSION and lives in the aggregate.

        builder.Ignore(d => d.IsCurrent);
    }
}

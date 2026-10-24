namespace Sankore.Modules.Leads.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Persistence;

/// <param name="encryptor">
/// M13's keyed encryptor, for the health answers. Applied by hand from <see cref="LeadsDbContext"/>
/// and excluded from the assembly scan, like the other two configurations that need a dependency.
/// </param>
internal sealed class QualificationResponseConfiguration(IFieldEncryptor encryptor)
    : IEntityTypeConfiguration<QualificationResponse>
{
    /// <summary>
    /// One response per person per questionnaire (BNF-B-08), for the rows answered on the lead
    /// itself. The aggregate cannot enforce this — responses are not children of the Lead
    /// aggregate — so the index IS the invariant.
    /// </summary>
    internal const string SubjectIndex = "ux_qualification_responses_subject";

    /// <summary>
    /// The same invariant per PROJECT — what makes the auto-creation of expected responses safe to
    /// run twice. Two indexes filtered on the project column, because PostgreSQL treats nulls as
    /// distinct: one index over a nullable project would let the lead-level rows duplicate.
    /// </summary>
    internal const string ProjectSubjectIndex = "ux_qualification_responses_project_subject";

    public void Configure(EntityTypeBuilder<QualificationResponse> builder)
    {
        builder.ToTable("qualification_responses");
        builder.HasKey(r => r.Id);

        // Non-health answers: plain jsonb, exactly as before.
        builder.Property<string>("PlainAnswersJson")
               .HasColumnName("answers")
               .HasColumnType("jsonb")
               .IsRequired();

        // Health answers: encrypted in bulk, NO blind index (BNF-B-09). Nothing searches for a
        // person by the answer they gave about their health, and an index would make "who answered
        // yes to question 7" a lookup.
        builder.Property<string?>("HealthAnswersJson")
               .HasColumnName("health_answers_enc")
               .HasConversion(EncryptedConverters.Text(encryptor));

        // Computed from whichever column holds the answers.
        builder.Ignore(r => r.AnswersJson);

        builder.Property(r => r.SubjectType).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.IsHealthData).HasDefaultValue(false);
        builder.Property(r => r.IsPurged).HasDefaultValue(false);

        builder.HasOne<Lead>()
               .WithMany()
               .HasForeignKey(r => r.LeadId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Opportunity>()
               .WithMany()
               .HasForeignKey(r => r.OpportunityId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => new { r.TenantId, r.LeadId });
        builder.HasIndex(r => new { r.TenantId, r.TemplateId });

        builder.HasIndex(r => new { r.LeadId, r.TemplateId, r.SubjectType, r.SubjectId })
               .IsUnique()
               .HasFilter("opportunity_id IS NULL")
               .HasDatabaseName(SubjectIndex);

        builder.HasIndex(r => new { r.OpportunityId, r.TemplateId, r.SubjectType, r.SubjectId })
               .IsUnique()
               .HasFilter("opportunity_id IS NOT NULL")
               .HasDatabaseName(ProjectSubjectIndex);

        // Serves the retention purge (BNF-B-09): health rows transmitted and not yet purged.
        // Partial, because health responses are a small minority of the table.
        builder.HasIndex(r => new { r.TenantId, r.TransmittedAt })
               .HasFilter("is_health_data AND NOT is_purged AND transmitted_at IS NOT NULL");
    }
}

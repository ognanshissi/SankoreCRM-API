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
        builder.Property(o => o.NextStep).HasMaxLength(200);
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
        JsonbList.Configure(builder.Property(o => o.CheckedChecklistItems));
        JsonbList.Configure(builder.Property(o => o.WorkflowRuns));
        builder.Property(o => o.CustomFieldsJson).HasColumnName("custom_fields").HasColumnType("jsonb");
        builder.Ignore(o => o.DomainEvents);
    }
}

internal sealed class ProjectAttachmentConfiguration : IEntityTypeConfiguration<ProjectAttachment>
{
    public void Configure(EntityTypeBuilder<ProjectAttachment> b)
    {
        b.ToTable("project_attachments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.FieldKey).HasMaxLength(60).IsRequired();
        b.Property(x => x.FileName).HasMaxLength(CrmTaskAttachment.FileNameMaxLength).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        b.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.StorageRef).HasMaxLength(80).IsRequired();
        b.Property(x => x.LastSentChannel).HasMaxLength(20);
        b.HasIndex(x => new { x.OpportunityId, x.FieldKey });
        b.HasOne<Opportunity>().WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ProjectHistoryEntryConfiguration : IEntityTypeConfiguration<ProjectHistoryEntry>
{
    public void Configure(EntityTypeBuilder<ProjectHistoryEntry> b)
    {
        b.ToTable("project_history");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.From).HasMaxLength(ProjectHistoryEntry.ValueMaxLength);
        b.Property(x => x.To).HasMaxLength(ProjectHistoryEntry.ValueMaxLength);
        b.Property(x => x.Detail).HasMaxLength(ProjectHistoryEntry.DetailMaxLength);
        b.HasIndex(x => new { x.OpportunityId, x.At });
        b.HasOne<Opportunity>().WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Quote lines, lot 3 (2026-11-10). Amounts in numeric(18,2), quantities (18,3), rates (5,2).</summary>
internal sealed class ProjectQuoteConfiguration : IEntityTypeConfiguration<ProjectQuote>
{
    public void Configure(EntityTypeBuilder<ProjectQuote> b)
    {
        b.ToTable("project_quotes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasIndex(x => x.OpportunityId).IsUnique();
        b.HasOne<Opportunity>().WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.GlobalDiscountKind).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.ApprovalStatus).HasConversion<string>().HasMaxLength(15);
        b.Property(x => x.DecisionComment).HasMaxLength(ProjectQuote.MaxDecisionCommentLength);
        foreach (var p in new[] { nameof(ProjectQuote.GlobalDiscountValue), nameof(ProjectQuote.SubtotalExclTax),
                     nameof(ProjectQuote.GlobalDiscountAmount), nameof(ProjectQuote.TotalExclTax),
                     nameof(ProjectQuote.TotalVat), nameof(ProjectQuote.TotalInclTax) })
            b.Property(p).HasPrecision(18, 2);
        b.Property(x => x.EffectiveDiscountPercent).HasPrecision(7, 2);
        b.Property(x => x.DiscountCeilingPercent).HasPrecision(5, 2);

        b.OwnsMany(x => x.Lines, l =>
        {
            l.ToTable("project_quote_lines");
            l.WithOwner().HasForeignKey("ProjectQuoteId");
            l.HasKey(x => x.Id);
            l.Property(x => x.Id).ValueGeneratedNever();
            l.Property(x => x.ProductCode).HasMaxLength(50).IsRequired();
            l.Property(x => x.Label).HasMaxLength(ProjectQuoteLine.MaxLabelLength).IsRequired();
            l.Property(x => x.Unit).HasMaxLength(ProjectQuoteLine.MaxUnitLength);
            l.Property(x => x.Quantity).HasPrecision(18, 3);
            l.Property(x => x.DiscountKind).HasConversion<string>().HasMaxLength(10);
            l.Property(x => x.VatCategory).HasConversion<string>().HasMaxLength(20);
            l.Property(x => x.VatRate).HasPrecision(5, 2);
            foreach (var p in new[] { nameof(ProjectQuoteLine.UnitPrice), nameof(ProjectQuoteLine.CatalogueUnitPrice),
                         nameof(ProjectQuoteLine.DiscountValue), nameof(ProjectQuoteLine.GrossExclTax),
                         nameof(ProjectQuoteLine.DiscountAmount), nameof(ProjectQuoteLine.NetExclTax),
                         nameof(ProjectQuoteLine.VatAmount), nameof(ProjectQuoteLine.TotalInclTax) })
                l.Property(p).HasPrecision(18, 2);
        });
        b.Navigation(x => x.Lines).AutoInclude();
        b.Property(x => x.Number).HasMaxLength(40);
        b.Ignore(x => x.ChangedSinceIssue);
    }
}

/// <summary>Quote lines, lot 5 (2026-11-10).</summary>
internal sealed class ProjectQuoteIssueConfiguration : IEntityTypeConfiguration<ProjectQuoteIssue>
{
    public void Configure(EntityTypeBuilder<ProjectQuoteIssue> b)
    {
        b.ToTable("project_quote_issues");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Number).HasMaxLength(40).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.TotalExclTax).HasPrecision(18, 2);
        b.Property(x => x.TotalInclTax).HasPrecision(18, 2);
        b.HasIndex(x => new { x.OpportunityId, x.Version }).IsUnique();
        b.HasOne<Opportunity>().WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
    }
}

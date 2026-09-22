namespace Sankore.Modules.Leads.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

internal sealed class QualificationTemplateConfiguration : IEntityTypeConfiguration<QualificationTemplate>
{
    public void Configure(EntityTypeBuilder<QualificationTemplate> builder)
    {
        builder.ToTable("qualification_templates");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name).HasMaxLength(200).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(1000);
        builder.Property(t => t.ProductCategory).HasMaxLength(30);
        builder.Property(t => t.ProductCode).HasMaxLength(50);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(t => t.Version).IsRequired();
        builder.Property(t => t.PublishedAt);

        // Domain events are transient.
        builder.Ignore(t => t.DomainEvents);

        // Sections — 1-to-many, separate table.
        builder.HasMany(t => t.Sections)
               .WithOne()
               .HasForeignKey(s => s.TemplateId)
               .OnDelete(DeleteBehavior.Cascade);

        // Questions — 1-to-many, separate table.
        builder.HasMany(t => t.Questions)
               .WithOne()
               .HasForeignKey(q => q.TemplateId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(t => new { t.TenantId, t.Status });

        // Enforces exactly one Published template per product type per tenant at the DB level.
        builder.HasIndex(t => new { t.TenantId, t.ProductCategory })
               .IsUnique()
               .HasFilter("product_category IS NOT NULL AND status = 'Published'");
    }
}

internal sealed class QualificationSectionConfiguration : IEntityTypeConfiguration<QualificationSection>
{
    public void Configure(EntityTypeBuilder<QualificationSection> builder)
    {
        builder.ToTable("qualification_sections");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Title).HasMaxLength(200).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(1000);

        builder.HasIndex(s => new { s.TemplateId, s.Order });
    }
}

internal sealed class QualificationQuestionConfiguration : IEntityTypeConfiguration<QualificationQuestion>
{
    public void Configure(EntityTypeBuilder<QualificationQuestion> builder)
    {
        builder.ToTable("qualification_questions");
        builder.HasKey(q => q.Id);

        builder.Property(q => q.Label).HasMaxLength(500).IsRequired();
        builder.Property(q => q.HelpText).HasMaxLength(1000);
        builder.Property(q => q.PlaceholderText).HasMaxLength(200);
        builder.Property(q => q.OptionsJson).HasColumnType("jsonb");
        builder.Property(q => q.RulesJson).HasColumnType("jsonb");
        builder.Property(q => q.Type).HasConversion<string>().HasMaxLength(20);
        builder.Property(q => q.MinValue).HasColumnType("numeric(18,4)");
        builder.Property(q => q.MaxValue).HasColumnType("numeric(18,4)");

        builder.HasIndex(q => new { q.TemplateId, q.Order });
    }
}

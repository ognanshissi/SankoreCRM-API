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
        builder.Property(t => t.ProductName).HasMaxLength(100);

        // Domain events are transient.
        builder.Ignore(t => t.DomainEvents);

        // Questions — owned 1-to-many, separate table.
        builder.HasMany(t => t.Questions)
               .WithOne()
               .HasForeignKey(q => q.TemplateId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(t => new { t.TenantId, t.IsActive });
        builder.HasIndex(t => new { t.TenantId, t.ProductName })
               .HasFilter("product_name IS NOT NULL");
    }
}

internal sealed class QualificationQuestionConfiguration : IEntityTypeConfiguration<QualificationQuestion>
{
    public void Configure(EntityTypeBuilder<QualificationQuestion> builder)
    {
        builder.ToTable("qualification_questions");
        builder.HasKey(q => q.Id);

        builder.Property(q => q.Label).HasMaxLength(500).IsRequired();
        builder.Property(q => q.OptionsJson).HasColumnType("jsonb");
        builder.Property(q => q.Type).HasConversion<string>().HasMaxLength(20);

        builder.HasIndex(q => new { q.TemplateId, q.Order });
    }
}

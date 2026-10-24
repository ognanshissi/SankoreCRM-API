using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

internal sealed class ProjectTemplateConfiguration : IEntityTypeConfiguration<ProjectTemplate>
{
    public void Configure(EntityTypeBuilder<ProjectTemplate> builder)
    {
        builder.ToTable("project_templates");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).HasMaxLength(ProjectTemplate.MaxNameLength).IsRequired();
        builder.Property(t => t.Category).HasConversion<string>().HasMaxLength(30);
        builder.Property(t => t.ProductCode).HasMaxLength(50);
        builder.Property(t => t.AmountKind).HasConversion<string>().HasMaxLength(20);

        // One template per scope. The handlers check first; these make a race lose instead of
        // leaving two templates that both claim the same products.
        builder.HasIndex(t => new { t.TenantId, t.ProductCode })
            .IsUnique()
            .HasFilter("product_code IS NOT NULL")
            .HasDatabaseName("ux_project_templates_product");
        builder.HasIndex(t => new { t.TenantId, t.Category })
            .IsUnique()
            .HasFilter("category IS NOT NULL AND product_code IS NULL")
            .HasDatabaseName("ux_project_templates_category");
        builder.HasIndex(t => t.TenantId)
            .IsUnique()
            .HasFilter("category IS NULL AND product_code IS NULL")
            .HasDatabaseName("ux_project_templates_default");

        builder.OwnsMany(t => t.Steps, s =>
        {
            s.ToTable("project_template_steps");
            s.WithOwner().HasForeignKey("TemplateId");
            // Keyed by (template, id), not by id alone: the built-in default's steps have fixed
            // ids, and every tenant that saves its default keeps them.
            s.HasKey("TemplateId", nameof(ProjectTemplateStep.Id));
            s.Property(x => x.Id).ValueGeneratedNever();
            s.Property(x => x.Label).HasMaxLength(ProjectTemplate.MaxLabelLength).IsRequired();
            s.Property(x => x.Phase).HasConversion<string>().HasMaxLength(30);
            s.Property(x => x.Suggestion).HasMaxLength(ProjectTemplate.MaxSuggestionLength);
            s.Property(x => x.WorkflowCircuit).HasMaxLength(ProjectTemplate.MaxCircuitLength);
            JsonbList.Configure(s.Property(x => x.Checklist));
        });
        builder.Navigation(t => t.Steps).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(t => t.IsDefault);
        builder.Ignore(t => t.DomainEvents);
    }
}

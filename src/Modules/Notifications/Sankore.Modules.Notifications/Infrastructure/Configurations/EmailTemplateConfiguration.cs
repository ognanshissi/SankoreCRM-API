namespace Sankore.Modules.Notifications.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Notifications.Domain;

internal sealed class EmailTemplateConfiguration
    : IEntityTypeConfiguration<EmailTemplate>
{
    public void Configure(EntityTypeBuilder<EmailTemplate> b)
    {
        b.ToTable("email_templates");
        b.HasKey(t => t.Id);

        b.Property(t => t.TemplateKey).HasMaxLength(100).IsRequired();
        b.Property(t => t.Locale).HasMaxLength(10).IsRequired();
        b.Property(t => t.Subject).HasMaxLength(500).IsRequired();

        // Only one active version per (TenantId, TemplateKey, Locale) combo enforced in application logic.
        b.HasIndex(t => new { t.TenantId, t.TemplateKey, t.Locale, t.Version }).IsUnique();
        b.HasIndex(t => new { t.TemplateKey, t.Locale, t.IsActive });
    }
}

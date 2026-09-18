namespace Sankore.Modules.Leads.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

internal sealed class QualificationResponseConfiguration : IEntityTypeConfiguration<QualificationResponse>
{
    public void Configure(EntityTypeBuilder<QualificationResponse> builder)
    {
        builder.ToTable("qualification_responses");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.AnswersJson).HasColumnType("jsonb").IsRequired();

        builder.HasOne<Lead>()
               .WithMany()
               .HasForeignKey(r => r.LeadId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => new { r.TenantId, r.LeadId });
        builder.HasIndex(r => new { r.TenantId, r.TemplateId });
    }
}

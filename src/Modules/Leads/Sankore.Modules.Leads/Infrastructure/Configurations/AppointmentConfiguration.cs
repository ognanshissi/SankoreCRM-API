using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

internal sealed class AppointmentConfiguration : IEntityTypeConfiguration<Appointment>
{
    public void Configure(EntityTypeBuilder<Appointment> builder)
    {
        builder.ToTable("appointments");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.Title).HasMaxLength(Appointment.TitleMaxLength).IsRequired();
        builder.Property(a => a.Description).HasMaxLength(Appointment.DescriptionMaxLength);
        builder.Property(a => a.TimeZone).HasMaxLength(64).IsRequired();
        builder.Property(a => a.Location).HasMaxLength(Appointment.LocationMaxLength);
        builder.Property(a => a.MeetingUrl).HasMaxLength(Appointment.MeetingUrlMaxLength);
        builder.Property(a => a.CancellationReason).HasMaxLength(500);

        builder.Property(a => a.Mode).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(a => a.MeetingProvider).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        // uuid[] : « mes rendez-vous » se lit `participant_ids @> {me}` ou `= ANY`, sans table de jointure.
        builder.Property(a => a.ParticipantIds).HasColumnType("uuid[]");

        builder.Ignore(a => a.EndAt);

        // No foreign key to the lead on purpose: an appointment outlives a merged or anonymised lead,
        // and the agenda reads the lead by id and degrades to « Lead » when it is gone.
        builder.HasIndex(a => new { a.TenantId, a.StartAt, a.Status });
        builder.HasIndex(a => new { a.TenantId, a.OrganizerId });
        builder.HasIndex(a => a.LeadId);
        builder.HasIndex(a => a.ParticipantIds).HasMethod("gin");
    }
}

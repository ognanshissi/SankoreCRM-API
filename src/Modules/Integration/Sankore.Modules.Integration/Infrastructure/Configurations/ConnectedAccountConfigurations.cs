namespace Sankore.Modules.Integration.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Integration.Domain;

/// <summary>
/// The `cal_` family: a user's connected Microsoft and Google accounts (connected agendas). The
/// tokens are in the secrets vault, never in these tables.
/// </summary>
internal sealed class ConnectedAccountConfiguration : IEntityTypeConfiguration<ConnectedAccount>
{
    public void Configure(EntityTypeBuilder<ConnectedAccount> b)
    {
        b.ToTable("cal_connected_account");
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedNever();

        b.Property(a => a.Provider).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(a => a.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(a => a.ExternalAccountId).HasMaxLength(200).IsRequired();
        b.Property(a => a.Email).HasMaxLength(320).IsRequired();
        b.Property(a => a.DisplayName).HasMaxLength(200);
        b.Property(a => a.Label).HasMaxLength(ConnectedAccount.LabelMaxLength).IsRequired();
        b.Property(a => a.GrantedScopes).HasMaxLength(1000).IsRequired();
        b.Property(a => a.LastError).HasMaxLength(60);
        b.Property(a => a.Version).IsRowVersion();

        // Le même compte connecté deux fois par la même personne est une reconnexion, pas un second compte.
        b.HasIndex(a => new { a.TenantId, a.UserId, a.Provider, a.ExternalAccountId })
            .IsUnique()
            .HasDatabaseName("ux_cal_connected_account_user_external");

        b.Ignore(a => a.DomainEvents);
    }
}

internal sealed class ConnectedAccountOAuthStateConfiguration : IEntityTypeConfiguration<ConnectedAccountOAuthState>
{
    public void Configure(EntityTypeBuilder<ConnectedAccountOAuthState> b)
    {
        b.ToTable("cal_oauth_state");
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();

        b.Property(s => s.Provider).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(s => s.StateHash).HasMaxLength(64).IsRequired();
        b.Property(s => s.Label).HasMaxLength(ConnectedAccount.LabelMaxLength);
        b.Property(s => s.ReturnOrigin).HasMaxLength(300).IsRequired();

        // Global, pas par tenant : le rappel du fournisseur trouve la ligne AVANT de connaître le tenant.
        b.HasIndex(s => s.StateHash).IsUnique().HasDatabaseName("ux_cal_oauth_state_hash");

        b.Ignore(s => s.DomainEvents);
    }
}

internal sealed class ConnectedCalendarConfiguration : IEntityTypeConfiguration<ConnectedCalendar>
{
    public void Configure(EntityTypeBuilder<ConnectedCalendar> b)
    {
        b.ToTable("cal_calendar");
        b.HasKey(c => c.Id);
        b.Property(c => c.Id).ValueGeneratedNever();

        b.Property(c => c.ExternalCalendarId).HasMaxLength(500).IsRequired();
        b.Property(c => c.Name).HasMaxLength(200).IsRequired();
        b.Property(c => c.Color).HasMaxLength(20);
        b.Property(c => c.SyncCursor).HasMaxLength(4000);
        b.Property(c => c.LastError).HasMaxLength(60);

        b.HasIndex(c => new { c.TenantId, c.AccountId, c.ExternalCalendarId })
            .IsUnique()
            .HasDatabaseName("ux_cal_calendar_account_external");

        b.HasIndex(c => new { c.TenantId, c.UserId });

        // One target agenda per user (lot 4).
        b.HasIndex(c => new { c.TenantId, c.UserId }, "ux_cal_calendar_appointment_target")
            .IsUnique()
            .HasFilter("is_appointment_target")
            .HasDatabaseName("ux_cal_calendar_appointment_target");

        b.Ignore(c => c.DomainEvents);
    }
}

internal sealed class ConnectedCalendarEventConfiguration : IEntityTypeConfiguration<ConnectedCalendarEvent>
{
    public void Configure(EntityTypeBuilder<ConnectedCalendarEvent> b)
    {
        b.ToTable("cal_event");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedNever();

        b.Property(e => e.ExternalEventId).HasMaxLength(1000).IsRequired();
        b.Property(e => e.ShowAs).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Ciphertext, unbounded: a 300-character title encrypts to more than 300.
        b.Property(e => e.TitleEnc).HasColumnName("title_enc");
        b.Property(e => e.LocationEnc).HasColumnName("location_enc");

        b.HasIndex(e => new { e.TenantId, e.CalendarId, e.ExternalEventId })
            .IsUnique()
            .HasDatabaseName("ux_cal_event_calendar_external");

        // L'écran calendrier : les événements d'un utilisateur sur une période.
        b.HasIndex(e => new { e.TenantId, e.UserId, e.StartAt });

        b.Ignore(e => e.DomainEvents);
    }
}

internal sealed class AppointmentCalendarCopyConfiguration : IEntityTypeConfiguration<AppointmentCalendarCopy>
{
    public void Configure(EntityTypeBuilder<AppointmentCalendarCopy> b)
    {
        b.ToTable("cal_appointment_copy");
        b.HasKey(c => c.Id);
        b.Property(c => c.Id).ValueGeneratedNever();

        b.Property(c => c.ExternalEventId).HasMaxLength(1000);
        b.Property(c => c.ContentHash).HasMaxLength(64);
        b.Property(c => c.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(c => c.LastError).HasMaxLength(60);

        b.HasIndex(c => new { c.TenantId, c.AppointmentId, c.UserId })
            .IsUnique()
            .HasDatabaseName("ux_cal_appointment_copy_appointment_user");

        // The echo filter of the calendar screen, and the retry sweep.
        b.HasIndex(c => new { c.TenantId, c.CalendarId, c.ExternalEventId });
        b.HasIndex(c => new { c.TenantId, c.Status });

        b.Ignore(c => c.DomainEvents);
    }
}

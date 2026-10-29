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
        b.Property(a => a.MailLastError).HasMaxLength(60);
        b.Property(a => a.MailSignature).HasMaxLength(MailSignatures.MaxLength);
        b.Property(a => a.Version).IsRowVersion();
        b.Ignore(a => a.HasCalendars);

        // Le même compte connecté deux fois par la même personne est une reconnexion, pas un second compte.
        b.HasIndex(a => new { a.TenantId, a.UserId, a.Provider, a.ExternalAccountId })
            .IsUnique()
            .HasDatabaseName("ux_cal_connected_account_user_external");

        b.Ignore(a => a.DomainEvents);
    }
}

/// <summary>The IMAP/SMTP servers of an IMAP account (connected mailbox, lot 1) — the password is in the vault.</summary>
internal sealed class MailServerSettingsConfiguration : IEntityTypeConfiguration<MailServerSettings>
{
    public void Configure(EntityTypeBuilder<MailServerSettings> b)
    {
        b.ToTable("cal_mail_server");
        b.HasKey(m => m.AccountId);
        b.Property(m => m.AccountId).ValueGeneratedNever();

        b.Property(m => m.ImapHost).HasMaxLength(MailServerSettings.HostMaxLength).IsRequired();
        b.Property(m => m.SmtpHost).HasMaxLength(MailServerSettings.HostMaxLength).IsRequired();
        b.Property(m => m.ImapSecurity).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(m => m.SmtpSecurity).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(m => m.Username).HasMaxLength(MailServerSettings.UsernameMaxLength).IsRequired();
        b.Ignore(m => m.Endpoints);

        b.HasOne<ConnectedAccount>().WithOne().HasForeignKey<MailServerSettings>(m => m.AccountId).OnDelete(DeleteBehavior.Cascade);

        b.Ignore(m => m.DomainEvents);
    }
}

/// <summary>The e-mails kept from a connected mailbox (lot 2): everything a person wrote is ciphertext.</summary>
internal sealed class ConnectedMailMessageConfiguration : IEntityTypeConfiguration<ConnectedMailMessage>
{
    public void Configure(EntityTypeBuilder<ConnectedMailMessage> b)
    {
        b.ToTable("cal_mail_message");
        b.HasKey(m => m.Id);
        b.Property(m => m.Id).ValueGeneratedNever();

        b.Property(m => m.ExternalId).HasMaxLength(500).IsRequired();
        b.Property(m => m.InternetMessageId).HasMaxLength(1000);
        b.Property(m => m.ThreadKey).HasMaxLength(1000).IsRequired();
        b.Property(m => m.Folder).HasConversion<string>().HasMaxLength(10).IsRequired();
        b.Property(m => m.Direction).HasConversion<string>().HasMaxLength(10).IsRequired();

        // Le même message lu deux fois (une page rejouée, un curseur repris) reste un seul message.
        b.HasIndex(m => new { m.TenantId, m.AccountId, m.ExternalId })
            .IsUnique()
            .HasDatabaseName("ux_cal_mail_message_account_external");

        b.HasIndex(m => new { m.TenantId, m.UserId, m.SentAt });
        b.HasIndex(m => new { m.TenantId, m.AccountId, m.ThreadKey });

        b.HasOne<ConnectedAccount>().WithMany().HasForeignKey(m => m.AccountId).OnDelete(DeleteBehavior.Cascade);

        b.Ignore(m => m.DomainEvents);
    }
}

internal sealed class ConnectedMailLinkConfiguration : IEntityTypeConfiguration<ConnectedMailLink>
{
    public void Configure(EntityTypeBuilder<ConnectedMailLink> b)
    {
        b.ToTable("cal_mail_link");
        b.HasKey(l => new { l.MessageId, l.EntityType, l.EntityId });
        b.Property(l => l.EntityType).HasConversion<string>().HasMaxLength(10).IsRequired();
        b.Property(l => l.Direction).HasConversion<string>().HasMaxLength(10).IsRequired();

        // La fiche d'un lead ou d'un client lit ses échanges du plus récent au plus ancien.
        b.HasIndex(l => new { l.TenantId, l.EntityType, l.EntityId, l.SentAt });

        b.HasOne<ConnectedMailMessage>().WithMany().HasForeignKey(l => l.MessageId).OnDelete(DeleteBehavior.Cascade);

        b.Ignore(l => l.DomainEvents);
    }
}

internal sealed class MailTemplateConfiguration : IEntityTypeConfiguration<MailTemplate>
{
    public void Configure(EntityTypeBuilder<MailTemplate> b)
    {
        b.ToTable("cal_mail_template");
        b.HasKey(t => t.Id);
        b.Property(t => t.Id).ValueGeneratedNever();
        b.Property(t => t.Name).HasMaxLength(MailTemplate.NameMaxLength).IsRequired();
        b.Property(t => t.Subject).HasMaxLength(MailTemplate.SubjectMaxLength).IsRequired();
        b.Property(t => t.Body).IsRequired();
        b.HasIndex(t => new { t.TenantId, t.IsArchived, t.Name });
        b.Ignore(t => t.DomainEvents);
    }
}

internal sealed class ScheduledMailConfiguration : IEntityTypeConfiguration<ScheduledMail>
{
    public void Configure(EntityTypeBuilder<ScheduledMail> b)
    {
        b.ToTable("cal_mail_scheduled");
        b.HasKey(m => m.Id);
        b.Property(m => m.Id).ValueGeneratedNever();
        b.Property(m => m.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(m => m.PayloadEnc).IsRequired();
        b.Property(m => m.LastError).HasMaxLength(60);
        b.HasIndex(m => new { m.TenantId, m.UserId, m.SendAt });
        b.HasIndex(m => new { m.TenantId, m.AccountId });
        b.Ignore(m => m.DomainEvents);
    }
}

internal sealed class ScheduledMailAttachmentConfiguration : IEntityTypeConfiguration<ScheduledMailAttachment>
{
    public void Configure(EntityTypeBuilder<ScheduledMailAttachment> b)
    {
        b.ToTable("cal_mail_scheduled_attachment");
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedNever();
        b.Property(a => a.Name).HasMaxLength(255).IsRequired();
        b.Property(a => a.ContentType).HasMaxLength(200).IsRequired();
        b.Property(a => a.ContentEnc).IsRequired();
        b.HasOne<ScheduledMail>().WithMany().HasForeignKey(a => a.ScheduledMailId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(a => a.ScheduledMailId);
        b.Ignore(a => a.DomainEvents);
    }
}

internal sealed class MailboxSyncStateConfiguration : IEntityTypeConfiguration<MailboxSyncState>
{
    public void Configure(EntityTypeBuilder<MailboxSyncState> b)
    {
        b.ToTable("cal_mail_sync_state");
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();
        b.Property(s => s.Stream).HasMaxLength(20).IsRequired();
        b.Property(s => s.Cursor).HasMaxLength(4000);
        b.Property(s => s.LastError).HasMaxLength(60);

        b.HasIndex(s => new { s.TenantId, s.AccountId, s.Stream }).IsUnique().HasDatabaseName("ux_cal_mail_sync_state_stream");

        b.HasOne<ConnectedAccount>().WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);

        b.Ignore(s => s.DomainEvents);
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
        b.Property(s => s.IncludeMail).HasDefaultValue(false);

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

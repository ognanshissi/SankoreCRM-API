namespace Sankore.Modules.Leads.Infrastructure.Configurations;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Persistence;

/// <summary>
/// Applied BY HAND from <c>LeadsDbContext</c>, like <c>LeadConfiguration</c>: the customer's
/// number and profile name are encrypted by a value converter, which needs M13's encryptor.
/// </summary>
internal sealed class WhatsAppConversationConfiguration(IFieldEncryptor encryptor)
    : IEntityTypeConfiguration<WhatsAppConversation>
{
    public void Configure(EntityTypeBuilder<WhatsAppConversation> b)
    {
        b.ToTable("whatsapp_conversations");
        b.HasKey(c => c.Id);
        b.Property(c => c.Id).ValueGeneratedNever();

        b.Property(c => c.ContactPhone)
            .HasColumnName("contact_phone_enc")
            .HasConversion(EncryptedConverters.Text(encryptor)!)
            .IsRequired();

        b.Property(c => c.ContactName)
            .HasColumnName("contact_name_enc")
            .HasConversion(EncryptedConverters.Text(encryptor));

        b.Property(c => c.EscalationSummary)
            .HasColumnName("escalation_summary_enc")
            .HasConversion(EncryptedConverters.Text(encryptor));

        // The agent's own words: encrypted like every free text about a person.
        b.Property(c => c.CloseReason)
            .HasColumnName("close_reason_enc")
            .HasConversion(EncryptedConverters.Text(encryptor));

        b.Property(c => c.EscalationReason).HasMaxLength(60);

        // Codes only, never values — see WhatsAppConversation.BotCollectedFields. The default is
        // what lets the column be added to a table that already has rows.
        b.Property(c => c.BotCollectedFields).HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]");

        b.Property(c => c.PendingLeadFieldsJson)
            .HasColumnName("pending_lead_fields_enc")
            .HasConversion(EncryptedConverters.Text(encryptor));

        b.Property(c => c.ContactPhoneBlindIndex).HasMaxLength(128).IsRequired();
        b.Property(c => c.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(c => c.Version).IsRowVersion();

        // Une réponse ne porte qu'un numéro : c'est par lui, sur ce numéro de l'institution,
        // qu'elle retrouve sa conversation. Une seule conversation par paire et par tenant.
        b.HasIndex(c => new { c.TenantId, c.ConnectionId, c.ContactPhoneBlindIndex })
            .IsUnique()
            .HasDatabaseName("ux_whatsapp_conversations_contact");

        b.HasIndex(c => new { c.TenantId, c.LeadId });
        b.HasIndex(c => new { c.TenantId, c.ClientId });
        b.HasIndex(c => new { c.TenantId, c.Status, c.LastMessageAt });

        // The SLA sweep: the pending ones whose deadline passed.
        b.HasIndex(c => new { c.TenantId, c.Status, c.TakeoverDeadline });
        b.HasIndex(c => new { c.TenantId, c.OwnerUserId });
        b.HasIndex(c => new { c.TenantId, c.TargetAgentId });

        // Retention of conversations filed on no lead (WA-BE-21).
        b.HasIndex(c => new { c.TenantId, c.LeadId, c.LastMessageAt }).HasFilter("anonymized_at IS NULL");
    }
}

internal sealed class WhatsAppMessageConfiguration(IFieldEncryptor encryptor)
    : IEntityTypeConfiguration<WhatsAppMessage>
{
    public void Configure(EntityTypeBuilder<WhatsAppMessage> b)
    {
        b.ToTable("whatsapp_messages");
        b.HasKey(m => m.Id);
        b.Property(m => m.Id).ValueGeneratedNever();

        // Unbounded text: the ciphertext of a 4 096-character message is longer than 4 096.
        b.Property(m => m.Body).HasColumnName("body_enc").HasConversion(EncryptedConverters.Text(encryptor));
        b.Property(m => m.TemplateParameters).HasColumnName("template_parameters_enc").HasConversion(EncryptedConverters.Text(encryptor));
        b.Property(m => m.MediaFileName).HasColumnName("media_file_name_enc").HasConversion(EncryptedConverters.Text(encryptor));

        b.Property(m => m.Direction).HasConversion<string>().HasMaxLength(10).IsRequired();
        b.Property(m => m.Author).HasConversion<string>().HasMaxLength(10).IsRequired();
        b.Property(m => m.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(m => m.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        b.Property(m => m.TemplateName).HasMaxLength(512);
        b.Property(m => m.TemplateLanguage).HasMaxLength(20);
        b.Property(m => m.MediaId).HasMaxLength(200);
        b.Property(m => m.MediaStorageRef).HasMaxLength(80);
        b.Property(m => m.MediaSha256).HasMaxLength(64);
        b.Ignore(m => m.HasMedia);

        // The media purge: stored or provider-held media past their retention.
        b.HasIndex(m => new { m.TenantId, m.MediaStoredAt }).HasFilter("media_storage_ref IS NOT NULL");
        b.Property(m => m.MediaMimeType).HasMaxLength(100);
        b.Property(m => m.ProviderMessageId).HasMaxLength(200);
        b.Property(m => m.ReplyToProviderMessageId).HasMaxLength(200);
        b.Property(m => m.ErrorCode).HasMaxLength(60);
        b.Property(m => m.ErrorDetail).HasMaxLength(300);

        // L'idempotence de toute la réception : le wamid est l'identité d'un message.
        b.HasIndex(m => new { m.TenantId, m.ProviderMessageId })
            .IsUnique()
            .HasFilter("provider_message_id IS NOT NULL")
            .HasDatabaseName("ux_whatsapp_messages_provider_id");

        // Le fil, du plus récent au plus ancien, page par page.
        b.HasIndex(m => new { m.ConversationId, m.OccurredAt });
    }
}

internal sealed class WhatsAppTemplateConfiguration : IEntityTypeConfiguration<WhatsAppTemplate>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public void Configure(EntityTypeBuilder<WhatsAppTemplate> b)
    {
        b.ToTable("whatsapp_templates");
        b.HasKey(t => t.Id);
        b.Property(t => t.Id).ValueGeneratedNever();

        b.Property(t => t.Name).HasMaxLength(512).IsRequired();
        b.Property(t => t.Language).HasMaxLength(20).IsRequired();
        b.Property(t => t.Category).HasMaxLength(30).IsRequired();
        b.Property(t => t.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(t => t.ProviderTemplateId).HasMaxLength(100);
        b.Property(t => t.HeaderText).HasMaxLength(1024);
        b.Property(t => t.BodyText).IsRequired();
        b.Property(t => t.FooterText).HasMaxLength(1024);

        b.Property(t => t.Buttons).HasColumnType("text[]");
        b.Property(t => t.ProductLineIds).HasColumnType("uuid[]");

        // Typed jsonb with an explicit comparer — a list inside a converted property is otherwise
        // compared by reference and an edited mapping would be silently dropped (see
        // BeneficiaryPolicyComparer). The default is '[]', not '' which PostgreSQL refuses.
        b.Property(t => t.VariableMappings)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'[]'::jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<List<WhatsAppVariableMapping>>(v, Json) ?? new List<WhatsAppVariableMapping>())
            .Metadata.SetValueComparer(new ValueComparer<List<WhatsAppVariableMapping>>(
                (a, c) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(c, Json),
                v => JsonSerializer.Serialize(v, Json).GetHashCode(StringComparison.Ordinal),
                v => JsonSerializer.Deserialize<List<WhatsAppVariableMapping>>(JsonSerializer.Serialize(v, Json), Json)!));

        b.HasIndex(t => new { t.TenantId, t.ConnectionId, t.Name, t.Language })
            .IsUnique()
            .HasDatabaseName("ux_whatsapp_templates_name_language");
    }
}

/// <summary>
/// A bot profile's lists are typed jsonb read whole and never filtered on, with explicit
/// comparers — the BeneficiaryPolicy lesson: a list inside a converted property is otherwise
/// compared by reference. Nothing here is personal data, so nothing is encrypted.
/// </summary>
internal sealed class WhatsAppBotProfileConfiguration : IEntityTypeConfiguration<WhatsAppBotProfile>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public void Configure(EntityTypeBuilder<WhatsAppBotProfile> b)
    {
        b.ToTable("whatsapp_bot_profiles");
        b.HasKey(p => p.Id);
        b.Property(p => p.Id).ValueGeneratedNever();

        b.Property(p => p.WelcomeMessage).HasMaxLength(WhatsAppBotProfile.MessageMaxLength);
        b.Property(p => p.OutOfHoursMessage).HasMaxLength(WhatsAppBotProfile.MessageMaxLength);
        b.Property(p => p.Instructions).HasMaxLength(WhatsAppBotProfile.InstructionsMaxLength);
        b.Property(p => p.TimeZoneId).HasMaxLength(64).IsRequired();

        b.Property(p => p.FieldsToCollect).HasColumnType("text[]");
        b.Property(p => p.FieldsRequiredForLead).HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]");
        b.Property(p => p.EscalationKeywords).HasColumnType("text[]");

        Jsonb(b.Property(p => p.Faq));
        Jsonb(b.Property(p => p.AgentHours));

        b.Ignore(p => p.IsGeneral);

        // One row per save; the current settings are the highest version. Guid.Empty is the
        // general profile, so the index constrains it too (a null would not be).
        b.HasIndex(p => new { p.TenantId, p.ProductLineId, p.Version })
            .IsUnique()
            .HasDatabaseName("ux_whatsapp_bot_profiles_version");
    }

    private static void Jsonb<T>(PropertyBuilder<List<T>> property) => property
        .HasColumnType("jsonb")
        .HasDefaultValueSql("'[]'::jsonb")
        .HasConversion(
            v => JsonSerializer.Serialize(v, Json),
            v => JsonSerializer.Deserialize<List<T>>(v, Json) ?? new List<T>())
        .Metadata.SetValueComparer(new ValueComparer<List<T>>(
            (a, c) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(c, Json),
            v => JsonSerializer.Serialize(v, Json).GetHashCode(StringComparison.Ordinal),
            v => JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(v, Json), Json)!));
}

/// <summary>Applied BY HAND: the transcript, the answer and the tool calls are encrypted.</summary>
internal sealed class WhatsAppBotTurnConfiguration(IFieldEncryptor encryptor) : IEntityTypeConfiguration<WhatsAppBotTurn>
{
    public void Configure(EntityTypeBuilder<WhatsAppBotTurn> b)
    {
        b.ToTable("whatsapp_bot_turns");
        b.HasKey(t => t.Id);
        b.Property(t => t.Id).ValueGeneratedNever();

        b.Property(t => t.Input).HasColumnName("input_enc").HasConversion(EncryptedConverters.Text(encryptor));
        b.Property(t => t.Output).HasColumnName("output_enc").HasConversion(EncryptedConverters.Text(encryptor));
        b.Property(t => t.ToolCalls).HasColumnName("tool_calls_enc").HasConversion(EncryptedConverters.Text(encryptor));

        b.Property(t => t.Model).HasMaxLength(100).IsRequired();
        b.Property(t => t.Outcome).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(t => t.EscalationReason).HasMaxLength(60);
        b.Property(t => t.ErrorCode).HasMaxLength(60);

        // A redelivered request runs nothing twice.
        b.HasIndex(t => new { t.TenantId, t.InboundMessageId })
            .IsUnique()
            .HasDatabaseName("ux_whatsapp_bot_turns_inbound_message");

        b.HasIndex(t => new { t.TenantId, t.ConversationId, t.CreatedAt });
    }
}

/// <summary>The handover history the metrics read (WA-BE-22). Identifiers, instants and a code.</summary>
internal sealed class WhatsAppHandoverConfiguration : IEntityTypeConfiguration<WhatsAppHandover>
{
    public void Configure(EntityTypeBuilder<WhatsAppHandover> b)
    {
        b.ToTable("whatsapp_handovers");
        b.HasKey(h => h.Id);
        b.Property(h => h.Id).ValueGeneratedNever();
        b.Property(h => h.Reason).HasMaxLength(60).IsRequired();
        b.Property(h => h.EndedBy).HasConversion<string>().HasMaxLength(20);
        b.Ignore(h => h.IsOpen);

        b.HasIndex(h => new { h.TenantId, h.ConversationId }).HasFilter("ended_at IS NULL");
        b.HasIndex(h => new { h.TenantId, h.EscalatedAt });
    }
}

namespace Sankore.Modules.Notifications.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Versioned, locale-specific email template.
/// TenantId == null → platform-wide default template.
/// Multiple versions may exist per (TenantId, TemplateKey, Locale); only
/// the one with IsActive = true is used for rendering.
///
/// System templates (IsSystem = true) are seeded at startup and cannot be
/// modified directly. A tenant admin must <see cref="Fork"/> them first,
/// which creates a tenant-scoped mutable copy.
/// </summary>
public sealed class EmailTemplate
{
    public Guid Id { get; private set; }

    /// <summary>Null for platform-wide default templates.</summary>
    public Guid? TenantId { get; private set; }

    public string TemplateKey { get; private set; } = default!;
    public string Locale { get; private set; } = default!;
    public int Version { get; private set; }
    public string Subject { get; private set; } = default!;
    public string HtmlBody { get; private set; } = default!;
    public string? TextBody { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsSystem { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private EmailTemplate() { }

    public static EmailTemplate Create(
        Guid? tenantId,
        string templateKey,
        string locale,
        int version,
        string subject,
        string htmlBody,
        string? textBody = null,
        bool isSystem = false) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        TemplateKey = templateKey,
        Locale = locale,
        Version = version,
        Subject = subject,
        HtmlBody = htmlBody,
        TextBody = textBody,
        IsActive = true,
        IsSystem = isSystem,
        CreatedAt = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// Creates a tenant-scoped, mutable copy of this system template.
    /// The fork starts at Version 1 with IsSystem = false.
    /// </summary>
    public EmailTemplate Fork(Guid tenantId)
    {
        if (!IsSystem)
            throw new DomainException("NOT_A_SYSTEM_TEMPLATE");

        return new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TemplateKey = TemplateKey,
            Locale = Locale,
            Version = 1,
            Subject = Subject,
            HtmlBody = HtmlBody,
            TextBody = TextBody,
            IsActive = true,
            IsSystem = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public void EnsureMutable()
    {
        if (IsSystem)
            throw new DomainException("SYSTEM_TEMPLATE_READONLY");
    }

    public void Deactivate() => IsActive = false;
    public void Activate() => IsActive = true;
}

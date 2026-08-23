namespace Sankore.Modules.Notifications.Domain;

/// <summary>
/// Versioned, locale-specific email template.
/// TenantId == null → platform-wide default template.
/// Multiple versions may exist per (TenantId, TemplateKey, Locale); only
/// the one with IsActive = true is used for rendering.
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
    public DateTimeOffset CreatedAt { get; private set; }

    private EmailTemplate() { }

    public static EmailTemplate Create(
        Guid? tenantId,
        string templateKey,
        string locale,
        int version,
        string subject,
        string htmlBody,
        string? textBody = null) => new()
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
        CreatedAt = DateTimeOffset.UtcNow
    };

    public void Deactivate() => IsActive = false;
    public void Activate() => IsActive = true;
}

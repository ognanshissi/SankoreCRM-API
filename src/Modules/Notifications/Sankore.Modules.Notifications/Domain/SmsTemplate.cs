namespace Sankore.Modules.Notifications.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// A versioned, locale-specific SMS body.
///
/// <para>
/// <b>Its own table rather than reusing <see cref="EmailTemplate"/>.</b> An SMS has no subject and
/// no HTML, so a shared row would carry a meaningless <c>Subject</c> and plain text in a column
/// called <c>HtmlBody</c> — and every screen and query would then have to know which of its
/// columns to ignore. What an SMS has instead is a LENGTH BUDGET: a GSM-7 segment is 160
/// characters and a second segment doubles the bill, so the one validation that matters here has
/// no equivalent on the email side.
/// </para>
///
/// <para>
/// Same resolution rules as the email templates, deliberately — tenant override, then the platform
/// default in the same locale, then French — so an administrator who has learned one has learned
/// both.
/// </para>
/// </summary>
public sealed class SmsTemplate
{
    /// <summary>
    /// One GSM-7 segment. Not enforced as a hard limit: a tenant whose own wording runs to two
    /// segments is making a decision about their own bill, and refusing it would be this module
    /// deciding how an institution talks to its customers. It is what the write slice WARNS on,
    /// and what the seeded bodies are written to fit.
    /// </summary>
    public const int SingleSegmentLength = 160;

    public Guid Id { get; private set; }

    /// <summary>Null for a platform-wide default.</summary>
    public Guid? TenantId { get; private set; }

    public string TemplateKey { get; private set; } = default!;
    public string Locale { get; private set; } = default!;
    public int Version { get; private set; }

    /// <summary>
    /// The message, as a Scriban template. Plain text: no subject, no markup, no links the
    /// customer cannot type back.
    /// </summary>
    public string Body { get; private set; } = default!;

    public bool IsActive { get; private set; }
    public bool IsSystem { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private SmsTemplate() { }

    public static SmsTemplate Create(
        Guid? tenantId,
        string templateKey,
        string locale,
        int version,
        string body,
        bool isSystem = false) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TemplateKey = templateKey,
            Locale = locale,
            Version = version,
            Body = body,
            IsActive = true,
            IsSystem = isSystem,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    /// <summary>A tenant-scoped, mutable copy of a system template. Starts at version 1.</summary>
    public SmsTemplate Fork(Guid tenantId)
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
            Body = Body,
            IsActive = true,
            IsSystem = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    public void Deactivate() => IsActive = false;
}

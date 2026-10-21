namespace Sankore.Modules.Notifications.Domain;

/// <summary>
/// One in-app notification of one user — what the bell in the header lists (WA-FE-10).
///
/// <para>
/// <b>Generic on purpose.</b> WhatsApp is its first sender, but « a person must act on something »
/// is not WhatsApp's concern, and the bell is the CRM's, not a channel's. A module that wants to
/// tell somebody something calls <c>INotificationsModule.NotifyInAppAsync</c>; this module stores
/// the row and pushes a nudge on the realtime hub.
/// </para>
///
/// <para>
/// <b>What it carries is shown to the user it is for, and nobody else</b>: a title, one sentence,
/// and a link inside the CRM that the user then opens under their own permissions. It is not a
/// copy of anything — a WhatsApp message's text never lands here, only the fact that somebody
/// is waiting.
/// </para>
/// </summary>
public sealed class UserNotification
{
    public const int TitleMaxLength = 200;
    public const int BodyMaxLength = 500;
    public const int LinkMaxLength = 300;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>Dotted and stable — <c>whatsapp.escalation</c>. Picks the icon on the screen.</summary>
    public string Category { get; private set; } = default!;

    public string Title { get; private set; } = default!;
    public string? Body { get; private set; }

    /// <summary>A route of the CRM — <c>/leads/{id}/suivi/whatsapp</c>. Never an external URL.</summary>
    public string? Link { get; private set; }

    /// <summary>The sender's key: the same key twice is one notification, so a retry tells once.</summary>
    public string IdempotencyKey { get; private set; } = default!;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }

    private UserNotification() { }

    public static UserNotification Create(
        Guid tenantId, Guid userId, string category, string title, string? body, string? link,
        string idempotencyKey, DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Category = Clip(category, 60)!,
            Title = Clip(title, TitleMaxLength)!,
            Body = Clip(body, BodyMaxLength),
            // Only an internal route: a link a module composed from remote input must not become
            // a way to send a user off the platform from their own bell.
            Link = link is { Length: > 0 } l && l.StartsWith('/') && !l.StartsWith("//", StringComparison.Ordinal)
                ? Clip(l, LinkMaxLength)
                : null,
            IdempotencyKey = Clip(idempotencyKey, 200)!,
            CreatedAt = now,
        };

    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;

    private static string? Clip(string? value, int max)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];
}

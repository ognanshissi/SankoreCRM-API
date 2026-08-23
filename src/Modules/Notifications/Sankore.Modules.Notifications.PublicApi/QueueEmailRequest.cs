namespace Sankore.Modules.Notifications.PublicApi;

/// <summary>
/// Input for <see cref="INotificationsModule.QueueEmailAsync"/>.
/// </summary>
/// <param name="TemplateKey">Identifies the email template (e.g. "lead.assigned", "password.reset").</param>
/// <param name="RecipientEmail">Destination email address.</param>
/// <param name="RecipientName">Optional display name for the recipient.</param>
/// <param name="Module">Source module name, used for observability (e.g. "Leads", "Administration").</param>
/// <param name="Locale">BCP-47 locale code for template selection (e.g. "fr", "en"). Defaults to "fr".</param>
/// <param name="TemplateData">Key-value pairs injected into the template at render time.</param>
/// <param name="IdempotencyKey">Caller-supplied unique key; duplicate keys are silently ignored.</param>
/// <param name="TenantId">Tenant that owns this email — used for provider resolution and quota tracking.</param>
public sealed record QueueEmailRequest(
    string TemplateKey,
    string RecipientEmail,
    string? RecipientName,
    string Module,
    string Locale,
    Dictionary<string, object> TemplateData,
    string IdempotencyKey,
    Guid TenantId);

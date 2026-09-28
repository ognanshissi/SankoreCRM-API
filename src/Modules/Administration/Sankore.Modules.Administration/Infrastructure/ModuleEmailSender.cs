namespace Sankore.Modules.Administration.Infrastructure;

using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// The one place this module turns "tell this person something" into a queued email.
///
/// Every caller needs the same three things and gets them wrong in the same three ways: the
/// recipient's locale (preference, then tenant default, then French), the tenant's display name
/// in the body, and the rule that a mail server being down must never undo the decision that
/// triggered the mail. Centralising it keeps those answers identical across slices.
/// </summary>
internal sealed class ModuleEmailSender(
    INotificationsModule notifications,
    ITenantStore tenantStore,
    ILogger<ModuleEmailSender> logger)
{
    /// <summary>
    /// Queues <paramref name="templateKey"/> for <paramref name="recipient"/>. Adds
    /// <c>full_name</c> and <c>company_name</c> to the template data; <paramref name="extra"/>
    /// supplies whatever else the template needs.
    ///
    /// Never throws: a recipient without an address is logged and skipped, and a failure to
    /// queue is logged as an error. Returns whether the mail was actually queued, which is what
    /// makes the behaviour testable.
    /// </summary>
    public async Task<bool> SendAsync(
        Guid tenantId,
        AppUser recipient,
        string templateKey,
        string idempotencyKey,
        IReadOnlyDictionary<string, object>? extra,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(recipient.Email))
        {
            logger.LogWarning(
                "User {UserId} has no email address; '{Template}' notification skipped.",
                recipient.Id, templateKey);
            return false;
        }

        try
        {
            var tenantInfo = await tenantStore.GetAsync(tenantId, ct);
            var locale = recipient.PreferredLanguage ?? tenantInfo?.DefaultLanguage ?? "fr";

            var data = new Dictionary<string, object>
            {
                ["full_name"] = recipient.FullName,
                ["company_name"] = tenantInfo?.Name ?? string.Empty,
            };

            if (extra is not null)
            {
                foreach (var (key, value) in extra)
                    data[key] = value;
            }

            await notifications.QueueEmailAsync(new QueueEmailRequest(
                TemplateKey: templateKey,
                RecipientEmail: recipient.Email,
                RecipientName: recipient.FullName,
                Module: "Administration",
                Locale: locale,
                TemplateData: data,
                IdempotencyKey: idempotencyKey,
                TenantId: tenantId), ct);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to queue '{Template}' for user {UserId}; the change itself stands.",
                templateKey, recipient.Id);
            return false;
        }
    }
}

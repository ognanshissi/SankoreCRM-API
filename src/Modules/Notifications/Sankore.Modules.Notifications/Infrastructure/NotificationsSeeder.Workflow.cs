namespace Sankore.Modules.Notifications.Infrastructure;

using Microsoft.Extensions.Logging;

/// <summary>
/// The message of a workflow « Notification » step (2026-10-29). Its text is written by a tenant's
/// administrator in the journey builder, its variables already replaced by the sending module — so
/// the template only frames it, and escapes it: a designer's message is not HTML.
/// Variables: subject, body, company_name.
/// </summary>
internal static partial class NotificationsSeeder
{
    private static async Task SeedWorkflowNotificationAsync(NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        const string body = """
              <div style="white-space:pre-wrap;line-height:1.5">{{ body | html.escape }}</div>
              {{ if company_name != "" }}<p style="margin-top:24px;color:#666;font-size:13px">{{ company_name | html.escape }}</p>{{ end }}
            """;

        await SeedOneAsync(db, logger, "workflow.notification", "fr", "{{ subject }}", body, ct);
        await SeedOneAsync(db, logger, "workflow.notification", "en", "{{ subject }}", body, ct);
    }
}

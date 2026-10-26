namespace Sankore.Modules.Notifications.Infrastructure;

using Microsoft.Extensions.Logging;

/// <summary>
/// The client-complaint e-mail (2026-10-26), sent by Customers' <c>ClientComplaintNotificationsConsumer</c>
/// to the agent who must handle a complaint, or to the agency manager when a deadline has passed.
/// One template for the three situations: the consumer passes the sentence that differs. Never
/// what the client wrote — only the operator's subject line.
/// </summary>
internal static partial class NotificationsSeeder
{
    private static async Task SeedClientComplaintTemplatesAsync(NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        // Variables: complaint_reference, complaint_subject, client_name, situation, due_at, complaint_url.
        await SeedOneAsync(db, logger, "client-complaint.action-required", "fr",
            "Réclamation {{ complaint_reference }} — {{ client_name }}",
            """
              <h2 style="color:#2563eb">Réclamation {{ complaint_reference }}</h2>
              <p>{{ situation | html.escape }}</p>
              <table style="border-collapse:collapse;width:100%;margin:16px 0">
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Client</td><td style="padding:8px;border:1px solid #eee">{{ client_name | html.escape }}</td></tr>
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Objet</td><td style="padding:8px;border:1px solid #eee">{{ complaint_subject | html.escape }}</td></tr>
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Échéance</td><td style="padding:8px;border:1px solid #eee">{{ due_at }}</td></tr>
              </table>
              <p><a href="{{ complaint_url | html.escape }}">Ouvrir les réclamations du client</a></p>
            """, ct);
        await SeedOneAsync(db, logger, "client-complaint.action-required", "en",
            "Complaint {{ complaint_reference }} — {{ client_name }}",
            """
              <h2 style="color:#2563eb">Complaint {{ complaint_reference }}</h2>
              <p>{{ situation | html.escape }}</p>
              <table style="border-collapse:collapse;width:100%;margin:16px 0">
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Client</td><td style="padding:8px;border:1px solid #eee">{{ client_name | html.escape }}</td></tr>
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Subject</td><td style="padding:8px;border:1px solid #eee">{{ complaint_subject | html.escape }}</td></tr>
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Due</td><td style="padding:8px;border:1px solid #eee">{{ due_at }}</td></tr>
              </table>
              <p><a href="{{ complaint_url | html.escape }}">Open the client's complaints</a></p>
            """, ct);
    }
}

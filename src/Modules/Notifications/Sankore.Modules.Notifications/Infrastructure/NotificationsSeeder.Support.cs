namespace Sankore.Modules.Notifications.Infrastructure;

using Microsoft.Extensions.Logging;

/// <summary>
/// The support-ticket e-mails, sent by Administration's <c>SupportNotifier</c>. The first two go
/// to the platform team, the last two to the tenant user who opened the ticket. Every free value
/// is escaped: subjects and messages are typed by people and land in someone else's mailbox.
/// </summary>
internal static partial class NotificationsSeeder
{
    private static async Task SeedSupportTemplatesAsync(NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        // ── To the platform team ──
        // Variables: company_name, reference, subject, category, priority, requested_by, excerpt, admin_url.
        await SeedOneAsync(db, logger, "support-ticket.opened", "fr",
            "[{{ reference }}] {{ company_name }} — {{ subject }}",
            """
              <h2 style="color:#2563eb">Nouveau ticket de support</h2>
              <p><strong>{{ company_name | html.escape }}</strong> a ouvert le ticket
                 <strong>{{ reference }}</strong> : {{ subject | html.escape }}.</p>
              <table style="border-collapse:collapse;width:100%;margin:16px 0">
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Demandé par</td><td style="padding:8px;border:1px solid #eee">{{ requested_by | html.escape }}</td></tr>
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Catégorie</td><td style="padding:8px;border:1px solid #eee">{{ category | html.escape }}</td></tr>
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Priorité</td><td style="padding:8px;border:1px solid #eee">{{ priority | html.escape }}</td></tr>
              </table>
              <blockquote style="border-left:3px solid #ddd;margin:0;padding:8px 16px;color:#444;white-space:pre-wrap">{{ excerpt | html.escape }}</blockquote>
              {{ if admin_url != "" }}<p><a href="{{ admin_url | html.escape }}">Ouvrir le ticket</a></p>{{ else }}<p>Le ticket attend dans l'application d'administration, rubrique « Support ».</p>{{ end }}
            """, ct);
        await SeedOneAsync(db, logger, "support-ticket.opened", "en",
            "[{{ reference }}] {{ company_name }} — {{ subject }}",
            """
              <h2 style="color:#2563eb">New support ticket</h2>
              <p><strong>{{ company_name | html.escape }}</strong> opened ticket
                 <strong>{{ reference }}</strong>: {{ subject | html.escape }}.</p>
              <table style="border-collapse:collapse;width:100%;margin:16px 0">
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Requested by</td><td style="padding:8px;border:1px solid #eee">{{ requested_by | html.escape }}</td></tr>
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Category</td><td style="padding:8px;border:1px solid #eee">{{ category | html.escape }}</td></tr>
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Priority</td><td style="padding:8px;border:1px solid #eee">{{ priority | html.escape }}</td></tr>
              </table>
              <blockquote style="border-left:3px solid #ddd;margin:0;padding:8px 16px;color:#444;white-space:pre-wrap">{{ excerpt | html.escape }}</blockquote>
              {{ if admin_url != "" }}<p><a href="{{ admin_url | html.escape }}">Open the ticket</a></p>{{ else }}<p>The ticket waits in the administration app, under « Support ».</p>{{ end }}
            """, ct);

        // Variables: company_name, reference, subject, author, excerpt, admin_url.
        await SeedOneAsync(db, logger, "support-ticket.tenant-replied", "fr",
            "[{{ reference }}] Nouvelle réponse de {{ company_name }}",
            """
              <h2 style="color:#2563eb">Le client a répondu</h2>
              <p>{{ author | html.escape }} ({{ company_name | html.escape }}) a répondu sur
                 <strong>{{ reference }}</strong> — {{ subject | html.escape }}.</p>
              <blockquote style="border-left:3px solid #ddd;margin:0;padding:8px 16px;color:#444;white-space:pre-wrap">{{ excerpt | html.escape }}</blockquote>
              {{ if admin_url != "" }}<p><a href="{{ admin_url | html.escape }}">Ouvrir le ticket</a></p>{{ end }}
            """, ct);
        await SeedOneAsync(db, logger, "support-ticket.tenant-replied", "en",
            "[{{ reference }}] New reply from {{ company_name }}",
            """
              <h2 style="color:#2563eb">The customer replied</h2>
              <p>{{ author | html.escape }} ({{ company_name | html.escape }}) replied on
                 <strong>{{ reference }}</strong> — {{ subject | html.escape }}.</p>
              <blockquote style="border-left:3px solid #ddd;margin:0;padding:8px 16px;color:#444;white-space:pre-wrap">{{ excerpt | html.escape }}</blockquote>
              {{ if admin_url != "" }}<p><a href="{{ admin_url | html.escape }}">Open the ticket</a></p>{{ end }}
            """, ct);

        // ── To the requester ──
        // Variables: full_name, company_name, reference, subject, author, excerpt, status_label, ticket_url.
        await SeedOneAsync(db, logger, "support-ticket.replied", "fr",
            "[{{ reference }}] Réponse du support Sankoré",
            """
              <h2 style="color:#2563eb">Le support vous a répondu</h2>
              <p>Bonjour {{ full_name | html.escape }},</p>
              <p>{{ author | html.escape }} a répondu à votre ticket <strong>{{ reference }}</strong>
                 — {{ subject | html.escape }}.</p>
              <blockquote style="border-left:3px solid #ddd;margin:0;padding:8px 16px;color:#444;white-space:pre-wrap">{{ excerpt | html.escape }}</blockquote>
              {{ if status_label != "" }}<p>Statut : <strong>{{ status_label | html.escape }}</strong></p>{{ end }}
              <p><a href="{{ ticket_url | html.escape }}">Voir le ticket et répondre</a></p>
            """, ct);
        await SeedOneAsync(db, logger, "support-ticket.replied", "en",
            "[{{ reference }}] Reply from Sankoré support",
            """
              <h2 style="color:#2563eb">Support replied</h2>
              <p>Hello {{ full_name | html.escape }},</p>
              <p>{{ author | html.escape }} replied to your ticket <strong>{{ reference }}</strong>
                 — {{ subject | html.escape }}.</p>
              <blockquote style="border-left:3px solid #ddd;margin:0;padding:8px 16px;color:#444;white-space:pre-wrap">{{ excerpt | html.escape }}</blockquote>
              {{ if status_label != "" }}<p>Status: <strong>{{ status_label | html.escape }}</strong></p>{{ end }}
              <p><a href="{{ ticket_url | html.escape }}">See the ticket and reply</a></p>
            """, ct);

        await SeedOneAsync(db, logger, "support-ticket.status-changed", "fr",
            "[{{ reference }}] {{ status_label }}",
            """
              <h2 style="color:#2563eb">Votre ticket a changé de statut</h2>
              <p>Bonjour {{ full_name | html.escape }},</p>
              <p>Votre ticket <strong>{{ reference }}</strong> — {{ subject | html.escape }} — est maintenant
                 <strong>{{ status_label | html.escape }}</strong>.</p>
              <p>Un ticket résolu se ferme de lui-même au bout de 7 jours sans réponse. S'il n'est pas réglé,
                 répondez simplement dessus.</p>
              <p><a href="{{ ticket_url | html.escape }}">Voir le ticket</a></p>
            """, ct);
        await SeedOneAsync(db, logger, "support-ticket.status-changed", "en",
            "[{{ reference }}] {{ status_label }}",
            """
              <h2 style="color:#2563eb">Your ticket's status changed</h2>
              <p>Hello {{ full_name | html.escape }},</p>
              <p>Your ticket <strong>{{ reference }}</strong> — {{ subject | html.escape }} — is now
                 <strong>{{ status_label | html.escape }}</strong>.</p>
              <p>A resolved ticket closes by itself after 7 days without a reply. If it is not settled,
                 just reply on it.</p>
              <p><a href="{{ ticket_url | html.escape }}">See the ticket</a></p>
            """, ct);
    }
}

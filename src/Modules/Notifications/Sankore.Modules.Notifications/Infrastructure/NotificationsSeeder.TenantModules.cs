namespace Sankore.Modules.Notifications.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Notifications.Domain;

/// <summary>
/// The paid-module e-mails (lot 4), sent by Administration's <c>TenantModuleNotifier</c>. The
/// first two go to the platform team, the last two to the tenant user who asked. Free values are
/// escaped: a refusal reason is typed by an operator and lands in a tenant's mailbox.
/// </summary>
internal static partial class NotificationsSeeder
{
    private const string MailHead = """
        <!DOCTYPE html>
        <html lang="{0}">
        <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
        """;

    private const string MailFoot = """
          <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
          <p style="color:#999;font-size:12px">Sankoré</p>
        </body>
        </html>
        """;

    private static async Task SeedTenantModuleTemplatesAsync(NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        // ── To the platform team ──
        // Variables: company_name, module_label, requested_by, accepted_price.
        await SeedOneAsync(db, logger, "tenant-module.requested", "fr",
            "{{ company_name }} demande le module {{ module_label }}",
            """
              <h2 style="color:#2563eb">Demande de module à examiner</h2>
              <p><strong>{{ company_name | html.escape }}</strong> demande le module
                 <strong>{{ module_label | html.escape }}</strong>.</p>
              <table style="border-collapse:collapse;width:100%;margin:16px 0">
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Demandé par</td><td style="padding:8px;border:1px solid #eee">{{ requested_by | html.escape }}</td></tr>
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Prix accepté</td><td style="padding:8px;border:1px solid #eee">{{ accepted_price }} / mois</td></tr>
              </table>
              <p>La demande attend votre décision dans l'application d'administration, rubrique « Demandes de modules ».</p>
            """, ct);
        await SeedOneAsync(db, logger, "tenant-module.requested", "en",
            "{{ company_name }} requests the {{ module_label }} module",
            """
              <h2 style="color:#2563eb">Module request to review</h2>
              <p><strong>{{ company_name | html.escape }}</strong> requests the
                 <strong>{{ module_label | html.escape }}</strong> module.</p>
              <table style="border-collapse:collapse;width:100%;margin:16px 0">
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Requested by</td><td style="padding:8px;border:1px solid #eee">{{ requested_by | html.escape }}</td></tr>
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Accepted price</td><td style="padding:8px;border:1px solid #eee">{{ accepted_price }} / month</td></tr>
              </table>
              <p>The request awaits your decision in the administration app, under « Demandes de modules ».</p>
            """, ct);

        // Variables: company_name, module_label, deactivated_by.
        await SeedOneAsync(db, logger, "tenant-module.deactivated", "fr",
            "{{ company_name }} a désactivé le module {{ module_label }}",
            """
              <h2 style="color:#d97706">Module désactivé</h2>
              <p><strong>{{ company_name | html.escape }}</strong> a désactivé le module
                 <strong>{{ module_label | html.escape }}</strong> ({{ deactivated_by | html.escape }}).</p>
              <p>L'effet est immédiat : ses écrans sont passés en consultation seule. À prendre en compte pour la prochaine facture.</p>
            """, ct);
        await SeedOneAsync(db, logger, "tenant-module.deactivated", "en",
            "{{ company_name }} deactivated the {{ module_label }} module",
            """
              <h2 style="color:#d97706">Module deactivated</h2>
              <p><strong>{{ company_name | html.escape }}</strong> deactivated the
                 <strong>{{ module_label | html.escape }}</strong> module ({{ deactivated_by | html.escape }}).</p>
              <p>It took effect immediately: its screens are now read-only. Mind it on the next invoice.</p>
            """, ct);

        // ── To the requester ──
        // Variables: full_name, company_name, module_label, accepted_price, modules_url.
        await SeedOneAsync(db, logger, "tenant-module.approved", "fr",
            "Le module {{ module_label }} est activé",
            """
              <h2 style="color:#16a34a">Module activé</h2>
              <p>Bonjour {{ full_name | html.escape }},</p>
              <p>L'équipe Sankoré a autorisé votre demande : le module <strong>{{ module_label | html.escape }}</strong>
                 est actif pour {{ company_name | html.escape }}{{ if accepted_price != "" }}, au prix de {{ accepted_price }} par mois{{ end }}.</p>
              <p><a href="{{ modules_url | html.escape }}">Voir les modules de votre institution</a></p>
            """, ct);
        await SeedOneAsync(db, logger, "tenant-module.approved", "en",
            "The {{ module_label }} module is active",
            """
              <h2 style="color:#16a34a">Module activated</h2>
              <p>Hello {{ full_name | html.escape }},</p>
              <p>The Sankoré team approved your request: the <strong>{{ module_label | html.escape }}</strong> module
                 is active for {{ company_name | html.escape }}{{ if accepted_price != "" }}, at {{ accepted_price }} per month{{ end }}.</p>
              <p><a href="{{ modules_url | html.escape }}">See your institution's modules</a></p>
            """, ct);

        // Variables: full_name, company_name, module_label, reason, modules_url.
        await SeedOneAsync(db, logger, "tenant-module.rejected", "fr",
            "Votre demande du module {{ module_label }} n'a pas été retenue",
            """
              <h2 style="color:#dc2626">Demande refusée</h2>
              <p>Bonjour {{ full_name | html.escape }},</p>
              <p>L'équipe Sankoré n'a pas autorisé le module <strong>{{ module_label | html.escape }}</strong>
                 pour {{ company_name | html.escape }}.</p>
              {{ if reason != "" }}<p><strong>Motif :</strong> {{ reason | html.escape }}</p>{{ end }}
              <p>Vous pouvez faire une nouvelle demande depuis la <a href="{{ modules_url | html.escape }}">page des modules</a>.</p>
            """, ct);
        await SeedOneAsync(db, logger, "tenant-module.rejected", "en",
            "Your request for the {{ module_label }} module was declined",
            """
              <h2 style="color:#dc2626">Request declined</h2>
              <p>Hello {{ full_name | html.escape }},</p>
              <p>The Sankoré team did not approve the <strong>{{ module_label | html.escape }}</strong> module
                 for {{ company_name | html.escape }}.</p>
              {{ if reason != "" }}<p><strong>Reason:</strong> {{ reason | html.escape }}</p>{{ end }}
              <p>You may ask again from the <a href="{{ modules_url | html.escape }}">modules page</a>.</p>
            """, ct);
    }

    private static async Task SeedOneAsync(NotificationsDbContext db, ILogger logger, string key, string locale,
        string subject, string body, CancellationToken ct)
    {
        var exists = await db.EmailTemplates
            .IgnoreQueryFilters()
            .AnyAsync(t => t.TemplateKey == key && t.TenantId == null && t.Locale == locale, ct);
        if (exists) return;

        db.EmailTemplates.Add(EmailTemplate.Create(
            tenantId: null,
            templateKey: key,
            locale: locale,
            version: 1,
            isSystem: true,
            subject: subject,
            htmlBody: string.Format(MailHead, locale) + body + MailFoot));

        logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [{Locale}]", key, locale);
    }
}

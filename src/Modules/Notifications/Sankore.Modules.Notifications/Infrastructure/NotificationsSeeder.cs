namespace Sankore.Modules.Notifications.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Notifications.Domain;

/// <summary>
/// Seeds platform-default email templates required by the system.
/// Runs at startup inside <see cref="NotificationsModule.InitializeAsync"/>.
/// Idempotent — only inserts templates that do not yet exist.
/// </summary>
internal static class NotificationsSeeder
{
    public static async Task SeedAsync(NotificationsDbContext db, ILogger logger, CancellationToken ct = default)
    {
        await SeedUserActivationAsync(db, logger, ct);
        await SeedPasswordForgotAsync(db, logger, ct);
        await SeedTaskAssignedAsync(db, logger, ct);
        await SeedLeadSlaBreachAsync(db, logger, ct);
        await SeedLeadSourceSnippetAsync(db, logger, ct);
        await db.SaveChangesAsync(ct);
    }

    // ── user.activation ─────────────────────────────────────────────────────

    private static async Task SeedUserActivationAsync(
        NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        const string key = "user.activation";

        var existing = await db.EmailTemplates
            .IgnoreQueryFilters()
            .Where(t => t.TemplateKey == key && t.TenantId == null)
            .Select(t => t.Locale)
            .ToListAsync(ct);

        if (!existing.Contains("fr"))
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                tenantId: null,
                templateKey: key,
                locale: "fr",
                version: 1,
                isSystem: true,
                subject: "Activez votre compte {{ company_name }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="fr">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Bienvenue, {{ full_name }} !</h2>
                      <p>Votre compte sur {{ company_name }} a été créé. Cliquez sur le bouton ci-dessous pour définir votre mot de passe et activer votre compte.</p>
                      <p style="margin:32px 0">
                        <a href="{{ activation_url }}"
                           style="background:#1a56db;color:#fff;padding:12px 24px;border-radius:6px;text-decoration:none;font-weight:600">
                          Activer mon compte
                        </a>
                      </p>
                      <p style="color:#666;font-size:13px">
                        Si le bouton ne fonctionne pas, copiez ce lien dans votre navigateur :<br>
                        <code>{{ activation_url }}</code>
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        Ce lien est valable 24 heures. Si vous n'attendiez pas ce message, ignorez-le.
                      </p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [fr]", key);
        }

        if (!existing.Contains("en"))
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                tenantId: null,
                templateKey: key,
                locale: "en",
                version: 1,
                isSystem: true,
                subject: "Activate your {{ company_name }} account",
                htmlBody: """
                    <!DOCTYPE html> 
                    <html lang="en">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Welcome, {{ full_name }}!</h2>
                      <p>Your {{ company_name }} account has been created. Click the button below to set your password and activate your account.</p>
                      <p style="margin:32px 0">
                        <a href="{{ activation_url }}" 
                           style="background:#1a56db;color:#fff;padding:12px 24px;border-radius:6px;text-decoration:none;font-weight:600">
                          Activate my account
                        </a>
                      </p>
                      <p style="color:#666;font-size:13px">
                        If the button does not work, copy this link into your browser:<br>
                        <code>{{ activation_url }}</code>
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        This link is valid for 24 hours. If you were not expecting this email, please ignore it.
                      </p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [en]", key);
        }
    }

    // ── user.password-forgot ─────────────────────────────────────────────────

    private static async Task SeedPasswordForgotAsync(
        NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        const string key = "user.password-forgot";

        var existing = await db.EmailTemplates
            .IgnoreQueryFilters()
            .Where(t => t.TemplateKey == key && t.TenantId == null)
            .Select(t => t.Locale)
            .ToListAsync(ct);

        if (!existing.Contains("fr"))
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                tenantId: null,
                templateKey: key,
                locale: "fr",
                version: 1,
                isSystem: true,
                subject: "Réinitialisation de votre mot de passe {{ company_name }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="fr">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Réinitialisation du mot de passe</h2>
                      <p>Bonjour {{ full_name }},</p>
                      <p>Nous avons reçu une demande de réinitialisation du mot de passe associé à votre compte {{ company_name }}.
                         Cliquez sur le bouton ci-dessous pour choisir un nouveau mot de passe.</p>
                      <p style="margin:32px 0">
                        <a href="{{ reset_url }}"
                           style="background:#1a56db;color:#fff;padding:12px 24px;border-radius:6px;text-decoration:none;font-weight:600">
                          Réinitialiser mon mot de passe
                        </a>
                      </p>
                      <p style="color:#666;font-size:13px">
                        Si le bouton ne fonctionne pas, copiez ce lien dans votre navigateur :<br>
                        <code>{{ reset_url }}</code>
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        Ce lien est valable 24 heures.<br>
                        Si vous n'avez pas demandé cette réinitialisation, ignorez ce message — votre mot de passe reste inchangé.
                      </p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [fr]", key);
        }

        if (!existing.Contains("en"))
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                tenantId: null,
                templateKey: key,
                locale: "en",
                version: 1,
                isSystem: true,
                subject: "Reset your {{ company_name }} password",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="en">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Password reset request</h2>
                      <p>Hello {{ full_name }},</p>
                      <p>We received a request to reset the password for your {{ company_name }} account.
                         Click the button below to choose a new password.</p>
                      <p style="margin:32px 0">
                        <a href="{{ reset_url }}"
                           style="background:#1a56db;color:#fff;padding:12px 24px;border-radius:6px;text-decoration:none;font-weight:600">
                          Reset my password
                        </a>
                      </p>
                      <p style="color:#666;font-size:13px">
                        If the button does not work, copy this link into your browser:<br>
                        <code>{{ reset_url }}</code>
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        This link is valid for 24 hours.<br>
                        If you did not request a password reset, you can safely ignore this email — your password will not change.
                      </p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [en]", key);
        }
    }

    // ── task.assigned ─────────────────────────────────────────────────────────
    // Variables: {{ agent_name }}, {{ task_title }}, {{ task_id }}, {{ due_at }}, {{ lead_id }}

    private static async Task SeedTaskAssignedAsync(
        NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        const string key = "task.assigned";

        var existing = await db.EmailTemplates
            .IgnoreQueryFilters()
            .Where(t => t.TemplateKey == key && t.TenantId == null)
            .Select(t => t.Locale)
            .ToListAsync(ct);

        if (!existing.Contains("fr"))
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                tenantId: null,
                templateKey: key,
                locale: "fr",
                version: 1,
                isSystem: true,
                subject: "Nouvelle tâche assignée : {{ task_title }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="fr">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Nouvelle tâche assignée</h2>
                      <p>Bonjour {{ agent_name }},</p>
                      <p>Une nouvelle tâche vous a été assignée :</p>
                      <table style="border-collapse:collapse;width:100%;margin:16px 0">
                        <tr><td style="padding:8px;border:1px solid #eee;font-weight:600">Titre</td><td style="padding:8px;border:1px solid #eee">{{ task_title }}</td></tr>
                        <tr><td style="padding:8px;border:1px solid #eee;font-weight:600">Échéance</td><td style="padding:8px;border:1px solid #eee">{{ due_at }}</td></tr>
                        <tr><td style="padding:8px;border:1px solid #eee;font-weight:600">Lead</td><td style="padding:8px;border:1px solid #eee">{{ lead_id }}</td></tr>
                      </table>
                      <p>Connectez-vous à la plateforme pour consulter les détails et démarrer la tâche.</p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">Réf. tâche : {{ task_id }}</p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [fr]", key);
        }

        if (!existing.Contains("en"))
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                tenantId: null,
                templateKey: key,
                locale: "en",
                version: 1,
                isSystem: true,
                subject: "New task assigned: {{ task_title }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="en">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>New task assigned</h2>
                      <p>Hello {{ agent_name }},</p>
                      <p>A new task has been assigned to you:</p>
                      <table style="border-collapse:collapse;width:100%;margin:16px 0">
                        <tr><td style="padding:8px;border:1px solid #eee;font-weight:600">Title</td><td style="padding:8px;border:1px solid #eee">{{ task_title }}</td></tr>
                        <tr><td style="padding:8px;border:1px solid #eee;font-weight:600">Due</td><td style="padding:8px;border:1px solid #eee">{{ due_at }}</td></tr>
                        <tr><td style="padding:8px;border:1px solid #eee;font-weight:600">Lead</td><td style="padding:8px;border:1px solid #eee">{{ lead_id }}</td></tr>
                      </table>
                      <p>Log in to the platform to view details and start the task.</p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">Task ref: {{ task_id }}</p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [en]", key);
        }
    }

    // ── lead.sla-breach ──────────────────────────────────────────────────────
    // Variables: {{ lead_name }}, {{ lead_id }}, {{ agent_name }}, {{ sla_deadline }}, {{ breach_hours }}

    private static async Task SeedLeadSlaBreachAsync(
        NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        const string key = "lead.sla-breach";

        var existing = await db.EmailTemplates
            .IgnoreQueryFilters()
            .Where(t => t.TemplateKey == key && t.TenantId == null)
            .Select(t => t.Locale)
            .ToListAsync(ct);

        if (!existing.Contains("fr"))
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                tenantId: null,
                templateKey: key,
                locale: "fr",
                version: 1,
                isSystem: true,
                subject: "⚠ Dépassement SLA — Lead {{ lead_name }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="fr">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2 style="color:#dc2626">Alerte SLA dépassé</h2>
                      <p>Bonjour {{ agent_name }},</p>
                      <p>Le délai de premier contact pour le lead suivant a été dépassé :</p>
                      <table style="border-collapse:collapse;width:100%;margin:16px 0">
                        <tr><td style="padding:8px;border:1px solid #eee;font-weight:600">Lead</td><td style="padding:8px;border:1px solid #eee">{{ lead_name }}</td></tr>
                        <tr><td style="padding:8px;border:1px solid #eee;font-weight:600">Échéance SLA</td><td style="padding:8px;border:1px solid #eee">{{ sla_deadline }}</td></tr>
                        <tr><td style="padding:8px;border:1px solid #eee;font-weight:600">Retard</td><td style="padding:8px;border:1px solid #eee">{{ breach_hours }} heure(s)</td></tr>
                      </table>
                      <p>Veuillez contacter ce lead dans les plus brefs délais.</p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">Réf. lead : {{ lead_id }}</p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [fr]", key);
        }

        if (!existing.Contains("en"))
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                tenantId: null,
                templateKey: key,
                locale: "en",
                version: 1,
                isSystem: true,
                subject: "⚠ SLA breach — Lead {{ lead_name }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="en">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2 style="color:#dc2626">SLA breach alert</h2>
                      <p>Hello {{ agent_name }},</p>
                      <p>The first-contact deadline for the following lead has been exceeded:</p>
                      <table style="border-collapse:collapse;width:100%;margin:16px 0">
                        <tr><td style="padding:8px;border:1px solid #eee;font-weight:600">Lead</td><td style="padding:8px;border:1px solid #eee">{{ lead_name }}</td></tr>
                        <tr><td style="padding:8px;border:1px solid #eee;font-weight:600">SLA deadline</td><td style="padding:8px;border:1px solid #eee">{{ sla_deadline }}</td></tr>
                        <tr><td style="padding:8px;border:1px solid #eee;font-weight:600">Overdue by</td><td style="padding:8px;border:1px solid #eee">{{ breach_hours }} hour(s)</td></tr>
                      </table>
                      <p>Please contact this lead as soon as possible.</p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">Lead ref: {{ lead_id }}</p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [en]", key);
        }
    }

    // ── lead-source.snippet ──────────────────────────────────────────────────
    // Variables: {{ source_label }}, {{ snippet }}, {{ sdk_url }}, {{ sri_hash }}, {{ public_key }}, {{ container_id }}

    private static async Task SeedLeadSourceSnippetAsync(
        NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        const string key = "lead-source.snippet";

        var existing = await db.EmailTemplates
            .IgnoreQueryFilters()
            .Where(t => t.TemplateKey == key && t.TenantId == null)
            .Select(t => t.Locale)
            .ToListAsync(ct);

        if (!existing.Contains("fr"))
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                tenantId: null,
                templateKey: key,
                locale: "fr",
                version: 1,
                isSystem: true,
                subject: "Script d'installation — {{ source_label }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="fr">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Script d'installation</h2>
                      <p>Voici le code à intégrer sur votre site pour la source <strong>{{ source_label }}</strong> :</p>
                      <pre style="background:#f5f5f5;padding:16px;border-radius:6px;overflow-x:auto;font-size:13px"><code>{{ snippet }}</code></pre>
                      <h3>Instructions</h3>
                      <ol>
                        <li>Copiez le code ci-dessus.</li>
                        <li>Collez-le juste avant la balise <code>&lt;/body&gt;</code> de votre page.</li>
                        <li>Le formulaire apparaîtra dans l'élément <code>#{{ container_id }}</code>.</li>
                      </ol>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">Clé publique : {{ public_key }}</p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [fr]", key);
        }

        if (!existing.Contains("en"))
        {
            db.EmailTemplates.Add(EmailTemplate.Create(
                tenantId: null,
                templateKey: key,
                locale: "en",
                version: 1,
                isSystem: true,
                subject: "Installation script — {{ source_label }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="en">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Installation script</h2>
                      <p>Here is the code to embed on your site for source <strong>{{ source_label }}</strong>:</p>
                      <pre style="background:#f5f5f5;padding:16px;border-radius:6px;overflow-x:auto;font-size:13px"><code>{{ snippet }}</code></pre>
                      <h3>Instructions</h3>
                      <ol>
                        <li>Copy the code above.</li>
                        <li>Paste it just before the <code>&lt;/body&gt;</code> tag of your page.</li>
                        <li>The form will render inside the <code>#{{ container_id }}</code> element.</li>
                      </ol>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">Public key: {{ public_key }}</p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [en]", key);
        }
    }
}

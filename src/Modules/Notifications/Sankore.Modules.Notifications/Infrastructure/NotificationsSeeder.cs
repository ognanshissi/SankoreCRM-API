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
        await SeedAgencyManagerAssignedAsync(db, logger, ct);
        await SeedAgencyManagerUnassignedAsync(db, logger, ct);
        await SeedAgentAbsenceDeclaredAsync(db, logger, ct);
        await SeedSuspiciousLoginReportedAsync(db, logger, ct);
        await SeedUserAgencyChangedAsync(db, logger, ct);
        await SeedUserRoleGrantedAsync(db, logger, ct);
        await SeedClientWelcomeAsync(db, logger, ct);
        await SeedHouseholdDesignationsToReviewAsync(db, logger, ct);
        await SeedClaimDocumentsRequiredAsync(db, logger, ct);
        await SeedComplaintDeadlineAsync(db, logger, ct);
        await SeedKycRemoteInvitationAsync(db, logger, ct);
        await SeedKycRemoteLinkExpiredAsync(db, logger, ct);
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
    // Variables: {{ full_name }}, {{ task_title }}, {{ task_id }}, {{ due_at }}, {{ lead_id }}

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
                      <p>Bonjour {{ full_name }},</p>
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
                      <p>Hello {{ full_name }},</p>
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
    // Variables: {{ lead_name }}, {{ lead_id }}, {{ full_name }}, {{ sla_deadline }}, {{ breach_hours }}

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
                      <p>Bonjour {{ full_name }},</p>
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
                      <p>Hello {{ full_name }},</p>
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

    // ── agent.absence-declared ──────────────────────────────────────────────
    // Destinataire : le supérieur de l'agent (AgentAbsenceNotifier, M12).

    private static async Task SeedAgentAbsenceDeclaredAsync(
        NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        const string key = "agent.absence-declared";

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
                subject: "{{ agent_name }} sera absent(e) du {{ absence_from }} au {{ absence_to }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="fr">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Bonjour {{ full_name }},</h2>
                      <p>
                        <strong>{{ agent_name }}</strong> a déclaré une absence du
                        <strong>{{ absence_from }}</strong> au <strong>{{ absence_to }}</strong>.
                        Aucun nouveau lead ne lui sera attribué pendant cette période.
                      </p>
                      <p>Remplaçant choisi&nbsp;: <strong>{{ replacement_name }}</strong>.</p>
                      <p>Tâches en cours transférées au remplaçant&nbsp;: {{ tasks_transferred }}.</p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">{{ company_name }}</p>
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
                subject: "{{ agent_name }} will be away from {{ absence_from }} to {{ absence_to }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="en">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Hello {{ full_name }},</h2>
                      <p>
                        <strong>{{ agent_name }}</strong> has declared an absence from
                        <strong>{{ absence_from }}</strong> to <strong>{{ absence_to }}</strong>.
                        No new lead will be assigned to them during that period.
                      </p>
                      <p>Chosen replacement: <strong>{{ replacement_name }}</strong>.</p>
                      <p>Open tasks transferred to the replacement: {{ tasks_transferred }}.</p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">{{ company_name }}</p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [en]", key);
        }
    }

    // ── agency.manager-assigned ─────────────────────────────────────────────

    private static async Task SeedAgencyManagerAssignedAsync(
        NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        const string key = "agency.manager-assigned";

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
                subject: "Vous dirigez désormais l'agence {{ agency_name }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="fr">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Bonjour {{ full_name }},</h2>
                      <p>
                        Vous venez d'être désigné(e) responsable de l'agence
                        <strong>{{ agency_name }}</strong> ({{ agency_code }}) de {{ company_name }}.
                      </p>
                      <p>
                        Le rôle <strong>{{ role_name }}</strong> a été ajouté à votre compte&nbsp;: il sera
                        actif à votre prochaine connexion.
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        Si cette affectation vous semble inattendue, contactez votre administrateur.
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
                subject: "You now manage the {{ agency_name }} agency",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="en">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Hello {{ full_name }},</h2>
                      <p>
                        You have just been made manager of the
                        <strong>{{ agency_name }}</strong> ({{ agency_code }}) agency at {{ company_name }}.
                      </p>
                      <p>
                        The <strong>{{ role_name }}</strong> role was added to your account; it takes
                        effect the next time you sign in.
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        If this assignment is unexpected, please contact your administrator.
                      </p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [en]", key);
        }
    }

    // ── agency.manager-unassigned ───────────────────────────────────────────

    private static async Task SeedAgencyManagerUnassignedAsync(
        NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        const string key = "agency.manager-unassigned";

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
                subject: "Vous ne dirigez plus l'agence {{ agency_name }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="fr">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Bonjour {{ full_name }},</h2>
                      <p>
                        La responsabilité de l'agence <strong>{{ agency_name }}</strong>
                        ({{ agency_code }}) de {{ company_name }} ne vous est plus confiée.
                      </p>
                      <p>
                        Les accès liés à cette agence seront ajustés à votre prochaine connexion.
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        Pour toute question sur ce changement, adressez-vous à votre administrateur.
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
                subject: "You no longer manage the {{ agency_name }} agency",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="en">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Hello {{ full_name }},</h2>
                      <p>
                        You are no longer responsible for the <strong>{{ agency_name }}</strong>
                        ({{ agency_code }}) agency at {{ company_name }}.
                      </p>
                      <p>
                        Access tied to that agency will be adjusted the next time you sign in.
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        If you have questions about this change, please contact your administrator.
                      </p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [en]", key);
        }
    }

    // ── user.agency-changed ─────────────────────────────────────────────────

    private static async Task SeedUserAgencyChangedAsync(
        NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        const string key = "user.agency-changed";

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
                subject: "Vous êtes désormais rattaché(e) à l'agence {{ agency_name }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="fr">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Bonjour {{ full_name }},</h2>
                      <p>
                        Votre rattachement a changé&nbsp;: vous dépendez désormais de l'agence
                        <strong>{{ agency_name }}</strong> ({{ agency_code }}) de {{ company_name }}.
                      </p>
                      <p>
                        Les données et les écrans accessibles depuis votre compte suivront ce
                        rattachement à votre prochaine connexion.
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        Si ce changement vous semble inattendu, contactez votre administrateur.
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
                subject: "You are now attached to the {{ agency_name }} agency",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="en">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Hello {{ full_name }},</h2>
                      <p>
                        Your home agency has changed: you now report to
                        <strong>{{ agency_name }}</strong> ({{ agency_code }}) at {{ company_name }}.
                      </p>
                      <p>
                        The data and screens your account can reach will follow that change the next
                        time you sign in.
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        If this change is unexpected, please contact your administrator.
                      </p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [en]", key);
        }
    }

    // ── user.role-granted ───────────────────────────────────────────────────

    private static async Task SeedUserRoleGrantedAsync(
        NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        const string key = "user.role-granted";

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
                subject: "Un nouveau rôle vous a été attribué : {{ role_name }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="fr">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Bonjour {{ full_name }},</h2>
                      <p>
                        Le rôle <strong>{{ role_name }}</strong> vient d'être ajouté à votre compte
                        {{ company_name }}.
                      </p>
                      <p>
                        Les écrans et les actions qu'il ouvre seront disponibles à votre prochaine
                        connexion.
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        Si cette attribution vous semble inattendue, contactez votre administrateur.
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
                subject: "A new role was assigned to you: {{ role_name }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="en">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Hello {{ full_name }},</h2>
                      <p>
                        The <strong>{{ role_name }}</strong> role has just been added to your
                        {{ company_name }} account.
                      </p>
                      <p>
                        The screens and actions it opens up become available the next time you sign in.
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        If this is unexpected, please contact your administrator.
                      </p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [en]", key);
        }
    }

    // ── client.welcome ──────────────────────────────────────────────────────

    private static async Task SeedClientWelcomeAsync(
        NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        const string key = "client.welcome";

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
                subject: "Bienvenue chez {{ company_name }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="fr">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Bienvenue {{ full_name }},</h2>
                      <p>
                        Votre dossier est ouvert chez {{ company_name }}. Votre numéro client est
                        <strong>{{ client_number }}</strong>&nbsp;: conservez-le, il vous sera demandé
                        au guichet et lors de vos échanges avec nos conseillers.
                      </p>
                      <p>
                        Votre conseiller reviendra vers vous pour finaliser les pièces nécessaires à
                        l'ouverture complète de votre compte.
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        Ce message vous est adressé parce qu'un dossier a été ouvert à votre nom. Si
                        vous n'en êtes pas à l'origine, contactez-nous sans tarder.
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
                subject: "Welcome to {{ company_name }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="en">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Welcome {{ full_name }},</h2>
                      <p>
                        Your file is now open at {{ company_name }}. Your client number is
                        <strong>{{ client_number }}</strong> — keep it to hand: you will be asked for it
                        at the counter and whenever you speak to one of our advisers.
                      </p>
                      <p>
                        Your adviser will come back to you about the documents needed to complete the
                        opening of your account.
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        You are receiving this because a file was opened in your name. If that was not
                        you, please contact us right away.
                      </p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [en]", key);
        }
    }

    // ── customers.household.designations_to_review ──────────────────────────
    // Variables: {{ client_number }}, {{ client_name }}, {{ designations_to_review }},
    //            {{ holdings_affected }}
    //
    // Carries NO identification of the departing member: a rendered body reaches a mail
    // provider and sits in the delivery log, and who belongs to a client's household is
    // exactly what this module keeps out of both.

    private static async Task SeedHouseholdDesignationsToReviewAsync(
        NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        const string key = "customers.household.designations_to_review";

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
                subject: "Clause bénéficiaire à revoir — client {{ client_number }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="fr">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Bonjour {{ full_name }},</h2>
                      <p>
                        Un membre du foyer du client <strong>{{ client_name }}</strong>
                        ({{ client_number }}) vient d'être retiré. Il restait
                        <strong>{{ designations_to_review }}</strong> désignation(s) bénéficiaire(s)
                        à son nom, sur {{ holdings_affected }} produit(s) détenu(s).
                      </p>
                      <p>
                        Ces désignations sont passées en <strong>à revoir</strong> : elles n'ont été
                        ni supprimées ni réattribuées, donc le rang auquel elles appartenaient
                        <strong>ne totalise plus 100 %</strong>. Une clause dans cet état ne se
                        découvre sinon qu'au moment du sinistre.
                      </p>
                      <p>Ouvrez la fiche du client pour reprendre la clause.</p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        Message automatique de {{ company_name }}.
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
                subject: "Beneficiary clause to review — client {{ client_number }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="en">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Hello {{ full_name }},</h2>
                      <p>
                        A household member of client <strong>{{ client_name }}</strong>
                        ({{ client_number }}) has just been removed. They still held
                        <strong>{{ designations_to_review }}</strong> beneficiary designation(s),
                        across {{ holdings_affected }} holding(s).
                      </p>
                      <p>
                        Those designations are now <strong>to review</strong>: they were neither
                        deleted nor reallocated, so the rank they belonged to
                        <strong>no longer totals 100%</strong>. A clause in that state is otherwise
                        only discovered at claim time.
                      </p>
                      <p>Open the client record to rework the clause.</p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        Automatic message from {{ company_name }}.
                      </p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [en]", key);
        }
    }
    // ── integration.claims.documents_required ───────────────────────────────

    /// <summary>
    /// ASS-09, fourth criterion: the insurer is waiting on pièces, and the agent who declared the
    /// loss is the one who can produce them.
    ///
    /// <para>
    /// Carries the insurer's dossier number, the date of the loss and the insurer's own list of
    /// documents — and deliberately NO customer name and no account of the loss. A rendered body
    /// travels to a mail provider and sits in its delivery log; what a death or an illness was is
    /// not something to put there to save an agent one click.
    /// </para>
    /// </summary>
    private static async Task SeedClaimDocumentsRequiredAsync(
        NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        const string key = "integration.claims.documents_required";

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
                subject: "Pièces attendues — sinistre {{ claim_number }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="fr">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Bonjour {{ full_name }},</h2>
                      <p>
                        L'assureur attend des pièces complémentaires pour le dossier
                        <strong>{{ claim_number }}</strong>, sinistre survenu le
                        {{ occurred_on }}.
                      </p>
                      <p style="background:#fff8e1;border-left:3px solid #f59e0b;padding:12px 16px">
                        <strong>Pièces demandées :</strong><br>
                        {{ missing_documents }}
                      </p>
                      <p>
                        Le dossier n'avance pas tant qu'elles ne sont pas transmises. Ouvrez-le
                        pour déposer les pièces.
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        Message automatique de {{ company_name }}.
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
                subject: "Documents required — claim {{ claim_number }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="en">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Hello {{ full_name }},</h2>
                      <p>
                        The insurer is waiting on further documents for claim
                        <strong>{{ claim_number }}</strong>, for the loss of {{ occurred_on }}.
                      </p>
                      <p style="background:#fff8e1;border-left:3px solid #f59e0b;padding:12px 16px">
                        <strong>Documents requested:</strong><br>
                        {{ missing_documents }}
                      </p>
                      <p>
                        The claim does not progress until they are sent. Open it to upload them.
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        Automatic message from {{ company_name }}.
                      </p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [en]", key);
        }
    }
    // ── integration.complaints.deadline_approaching ─────────────────────────

    /// <summary>
    /// The règlement 01/CIMA/2024's complaints mechanism, chased before it is breached.
    ///
    /// <para>
    /// Carries the reference, the subject and the date — and deliberately NOT what the customer
    /// said. A grievance is free text about a named person's money, health or family, and a
    /// rendered body travels to a mail provider and sits in its delivery log.
    /// </para>
    /// </summary>
    private static async Task SeedComplaintDeadlineAsync(
        NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        const string key = "integration.complaints.deadline_approaching";

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
                subject: "Réclamation {{ complaint_reference }} — échéance au {{ due_at }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="fr">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Bonjour {{ full_name }},</h2>
                      <p>
                        La réclamation <strong>{{ complaint_reference }}</strong> —
                        « {{ complaint_subject }} » — attend une action de votre part.
                      </p>
                      <p style="background:#fef2f2;border-left:3px solid #dc2626;padding:12px 16px">
                        Étape attendue : <strong>{{ stage }}</strong><br>
                        Échéance : <strong>{{ due_at }}</strong>
                      </p>
                      <p>
                        Le délai de traitement des réclamations fait partie du mécanisme exigé par
                        la réglementation CIMA sur la distribution d'assurance. Une échéance
                        dépassée se retrouve dans le rapport remis au superviseur.
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        Message automatique de {{ company_name }}.
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
                subject: "Complaint {{ complaint_reference }} — due {{ due_at }}",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="en">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Hello {{ full_name }},</h2>
                      <p>
                        Complaint <strong>{{ complaint_reference }}</strong> —
                        "{{ complaint_subject }}" — is waiting on you.
                      </p>
                      <p style="background:#fef2f2;border-left:3px solid #dc2626;padding:12px 16px">
                        Step owed: <strong>{{ stage }}</strong><br>
                        Due: <strong>{{ due_at }}</strong>
                      </p>
                      <p>
                        Complaint-handling deadlines are part of the mechanism the CIMA regulation
                        on insurance distribution requires. A missed one appears in the report
                        given to the supervisor.
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        Automatic message from {{ company_name }}.
                      </p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [en]", key);
        }
    }

    private static async Task SeedSuspiciousLoginReportedAsync(
        NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        const string key = "security.suspicious-login-reported";

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
                subject: "{{ user_name }} a signalé une connexion suspecte",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="fr">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Bonjour {{ full_name }},</h2>
                      <p>
                        <strong>{{ user_name }}</strong> ({{ user_email }}) indique qu'une connexion à son
                        compte ne vient pas de lui ou d'elle.
                      </p>
                      <ul>
                        <li>Connexion signalée&nbsp;: {{ login_at }}</li>
                        <li>Appareil&nbsp;: {{ login_device }}</li>
                        <li>Adresse IP&nbsp;: {{ login_ip }}</li>
                        <li>Commentaire&nbsp;: {{ comment }}</li>
                      </ul>
                      <p>
                        {{ ended_sessions }} autre(s) session(s) ont été fermées automatiquement.
                        Vérifiez avec la personne qu'elle a changé son mot de passe.
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">{{ company_name }}</p>
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
                subject: "{{ user_name }} reported a suspicious login",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="en">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Hello {{ full_name }},</h2>
                      <p>
                        <strong>{{ user_name }}</strong> ({{ user_email }}) reports that a login to their
                        account was not theirs.
                      </p>
                      <ul>
                        <li>Reported login: {{ login_at }}</li>
                        <li>Device: {{ login_device }}</li>
                        <li>IP address: {{ login_ip }}</li>
                        <li>Comment: {{ comment }}</li>
                      </ul>
                      <p>
                        {{ ended_sessions }} other session(s) were signed out automatically.
                        Please make sure they change their password.
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">{{ company_name }}</p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [en]", key);
        }
    }

    // ── kyc.remote-invitation ───────────────────────────────────────────────

    /// <summary>
    /// KRM-B-03: the second message a newly converted customer receives, carrying the single-use
    /// link to their own identification page.
    ///
    /// <para>
    /// <b>It carries the link and deliberately NOT the client number.</b> The number arrives in the
    /// welcome message, and the public page asks for it together with the date of birth — so the
    /// link is a credential and the number is the secret it is checked against. Putting both in
    /// one email would make a single intercepted message enough to walk the whole flow, which is
    /// precisely what two factors are there to prevent. The body says where to find the number
    /// instead.
    /// </para>
    ///
    /// <para>
    /// No name of a document, no score, no reading: nothing about the customer's identity. A
    /// rendered body travels to a mail provider and sits in its delivery log.
    /// </para>
    /// </summary>
    private static async Task SeedKycRemoteInvitationAsync(
        NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        const string key = "kyc.remote-invitation";

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
                subject: "{{ company_name }} — finalisez votre dossier depuis votre téléphone",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="fr">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Bonjour {{ client_first_name }},</h2>
                      <p>
                        Il ne manque plus que vos pièces d'identité pour finaliser votre dossier
                        chez {{ company_name }}. Vous pouvez les transmettre vous-même, en quelques
                        minutes, depuis votre téléphone.
                      </p>
                      <p style="margin:28px 0">
                        <a href="{{ link }}"
                           style="background:#1f6feb;color:#fff;text-decoration:none;padding:12px 20px;border-radius:6px;display:inline-block;font-weight:600">
                          Transmettre mes pièces
                        </a>
                      </p>
                      <p>
                        Ce lien est <strong>personnel</strong> et valable jusqu'au
                        <strong>{{ expires_at }}</strong>. Ne le transmettez à personne.
                      </p>
                      <p>
                        Il vous sera demandé votre <strong>numéro client</strong> et votre
                        <strong>date de naissance</strong>. Votre numéro client figure dans l'email
                        de bienvenue que nous venons de vous envoyer.
                      </p>
                      <p>
                        Prévoyez votre pièce d'identité à portée de main. Si vous préférez, vous
                        pouvez aussi passer à l'agence {{ agency_name }}.
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        Si vous n'êtes pas à l'origine de cette demande, ne cliquez pas sur le lien
                        et contactez-nous.
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
                subject: "{{ company_name }} — complete your file from your phone",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="en">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Hello {{ client_first_name }},</h2>
                      <p>
                        All that is missing to complete your file at {{ company_name }} is your
                        identity document. You can send it yourself, in a few minutes, from your
                        phone.
                      </p>
                      <p style="margin:28px 0">
                        <a href="{{ link }}"
                           style="background:#1f6feb;color:#fff;text-decoration:none;padding:12px 20px;border-radius:6px;display:inline-block;font-weight:600">
                          Send my documents
                        </a>
                      </p>
                      <p>
                        This link is <strong>personal</strong> and valid until
                        <strong>{{ expires_at }}</strong>. Do not forward it to anyone.
                      </p>
                      <p>
                        You will be asked for your <strong>client number</strong> and your
                        <strong>date of birth</strong>. Your client number is in the welcome email
                        we have just sent you.
                      </p>
                      <p>
                        Have your identity document to hand. If you would rather, you can also come
                        to the {{ agency_name }} branch.
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        If you did not expect this message, do not click the link and contact us.
                      </p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [en]", key);
        }
    }

    // ── kyc.remote-link-expired ─────────────────────────────────────────────

    /// <summary>
    /// KRM-B-04: the link a customer was sent lapsed without being used, and the agent who
    /// converted them is the one who can do something about it.
    ///
    /// <para>
    /// Carries the file id and the date, and NO name, number or contact detail of the customer:
    /// the agent opens the file and the file has everything. A rendered body sits in a mail
    /// provider's delivery log, and "who is being KYC'd" is not something to put there to save a
    /// click.
    /// </para>
    /// </summary>
    private static async Task SeedKycRemoteLinkExpiredAsync(
        NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        const string key = "kyc.remote-link-expired";

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
                subject: "Lien KYC à distance expiré — dossier à relancer",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="fr">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Bonjour {{ full_name }},</h2>
                      <p>
                        Le lien de dépôt des pièces envoyé à un client que vous avez converti a
                        expiré le <strong>{{ expired_at }}</strong> sans avoir été utilisé.
                      </p>
                      <p>
                        Le dossier reste en collecte : ouvrez-le pour renvoyer un lien, ou
                        convenez d'un passage en agence avec le client.
                      </p>
                      <p style="color:#555;font-size:13px">
                        Dossier KYC : <span style="font-family:monospace">{{ kyc_file_id }}</span>
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        Message automatique de {{ company_name }}.
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
                subject: "Remote KYC link expired — file needs a follow-up",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="en">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Hello {{ full_name }},</h2>
                      <p>
                        The document-collection link sent to a customer you converted expired on
                        <strong>{{ expired_at }}</strong> without being used.
                      </p>
                      <p>
                        The file is still collecting: open it to send a new link, or arrange a
                        branch visit with the customer.
                      </p>
                      <p style="color:#555;font-size:13px">
                        KYC file: <span style="font-family:monospace">{{ kyc_file_id }}</span>
                      </p>
                      <hr style="border:none;border-top:1px solid #eee;margin:32px 0">
                      <p style="color:#999;font-size:12px">
                        Automatic message from {{ company_name }}.
                      </p>
                    </body>
                    </html>
                    """));

            logger.LogInformation("NotificationsSeeder: seeded platform template '{Key}' [en]", key);
        }
    }
}

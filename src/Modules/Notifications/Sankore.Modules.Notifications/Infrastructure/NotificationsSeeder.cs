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
                subject: "Activez votre compte Sankore",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="fr">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Bienvenue, {{ full_name }} !</h2>
                      <p>Votre compte Sankore a été créé. Cliquez sur le bouton ci-dessous pour définir votre mot de passe et activer votre compte.</p>
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
                subject: "Activate your Sankore account",
                htmlBody: """
                    <!DOCTYPE html> 
                    <html lang="en">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Welcome, {{ full_name }}!</h2>
                      <p>Your Sankore account has been created. Click the button below to set your password and activate your account.</p>
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
                subject: "Réinitialisation de votre mot de passe Sankore",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="fr">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Réinitialisation du mot de passe</h2>
                      <p>Bonjour {{ full_name }},</p>
                      <p>Nous avons reçu une demande de réinitialisation du mot de passe associé à votre compte Sankore.
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
                subject: "Reset your Sankore password",
                htmlBody: """
                    <!DOCTYPE html>
                    <html lang="en">
                    <body style="font-family:sans-serif;color:#111;max-width:600px;margin:auto;padding:24px">
                      <h2>Password reset request</h2>
                      <p>Hello {{ full_name }},</p>
                      <p>We received a request to reset the password for your Sankore account.
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
}

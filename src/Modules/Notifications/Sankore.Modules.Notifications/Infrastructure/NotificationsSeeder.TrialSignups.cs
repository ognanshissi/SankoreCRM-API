namespace Sankore.Modules.Notifications.Infrastructure;

using Microsoft.Extensions.Logging;

/// <summary>
/// The free-trial e-mails (<c>docs/essai-gratuit-socle.md</c>, lot 2), sent by Sankore.Api's
/// <c>Features/TrialSignups</c>. Every value a visitor typed (names, institution, a refusal reason
/// typed by an operator) is escaped: it lands in somebody's mailbox.
/// </summary>
internal static partial class NotificationsSeeder
{
    private const string Button = """
        <p style="margin:24px 0"><a href="{0}" style="background:#2563eb;color:#fff;padding:12px 20px;border-radius:6px;text-decoration:none;display:inline-block">{1}</a></p>
        <p style="color:#666;font-size:13px">{2}<br><span style="word-break:break-all">{0}</span></p>
        """;

    private static async Task SeedTrialSignupTemplatesAsync(NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        // ── To the visitor: confirm the address ──
        // Variables: first_name, institution_name, crm_address, confirm_url, expires_hours.
        await SeedOneAsync(db, logger, "trial.signup-confirm", "fr",
            "Confirmez votre adresse pour ouvrir votre CRM",
            """
              <h2 style="color:#2563eb">Encore une étape</h2>
              <p>Bonjour {{ first_name | html.escape }},</p>
              <p>Vous avez demandé un essai gratuit du CRM pour <strong>{{ institution_name | html.escape }}</strong>,
                 à l'adresse <strong>{{ crm_address | html.escape }}</strong>.</p>
              <p>Confirmez votre adresse e-mail pour que nous puissions préparer votre CRM :</p>
            """ + string.Format(Button, "{{ confirm_url }}", "Confirmer mon adresse",
                "Le lien est valable {{ expires_hours }} heures. Si le bouton ne fonctionne pas, copiez ce lien :") + """
              <p style="color:#666;font-size:13px">Vous n'êtes pas à l'origine de cette demande ? Ignorez ce message : rien ne sera créé.</p>
            """, ct);
        await SeedOneAsync(db, logger, "trial.signup-confirm", "en",
            "Confirm your address to open your CRM",
            """
              <h2 style="color:#2563eb">One more step</h2>
              <p>Hello {{ first_name | html.escape }},</p>
              <p>You asked for a free trial of the CRM for <strong>{{ institution_name | html.escape }}</strong>,
                 at <strong>{{ crm_address | html.escape }}</strong>.</p>
              <p>Confirm your e-mail address so we can prepare your CRM:</p>
            """ + string.Format(Button, "{{ confirm_url }}", "Confirm my address",
                "The link is valid for {{ expires_hours }} hours. If the button does not work, copy this link:") + """
              <p style="color:#666;font-size:13px">Not you? Ignore this message: nothing will be created.</p>
            """, ct);

        // ── To the visitor whose address is already in use (the public answer stays the same) ──
        // Variables: none.
        await SeedOneAsync(db, logger, "trial.signup-existing", "fr",
            "Votre demande d'essai du CRM",
            """
              <h2 style="color:#2563eb">Une demande existe déjà</h2>
              <p>Bonjour,</p>
              <p>Nous avons reçu une demande d'essai gratuit du CRM avec votre adresse e-mail. Cette adresse
                 administre déjà un CRM, ou une demande est déjà en cours avec elle.</p>
              <p>Pour vous connecter, ouvrez l'adresse de votre CRM et utilisez « Mot de passe oublié » si besoin.
                 Pour toute question, répondez simplement à ce message.</p>
              <p style="color:#666;font-size:13px">Vous n'êtes pas à l'origine de cette demande ? Ignorez ce message.</p>
            """, ct);
        await SeedOneAsync(db, logger, "trial.signup-existing", "en",
            "Your CRM trial request",
            """
              <h2 style="color:#2563eb">A request already exists</h2>
              <p>Hello,</p>
              <p>We received a free CRM trial request with your e-mail address. This address already
                 administers a CRM, or a request is already in progress with it.</p>
              <p>To sign in, open your CRM's address and use « Forgot password » if needed.
                 For any question, simply reply to this message.</p>
              <p style="color:#666;font-size:13px">Not you? Ignore this message.</p>
            """, ct);

        // ── To the platform team: a confirmed request awaits its decision (D3) ──
        // Variables: institution_name, full_name, email, phone, country, activity_type, crm_address.
        await SeedOneAsync(db, logger, "trial.signup-received", "fr",
            "Demande d'essai : {{ institution_name }}",
            """
              <h2 style="color:#2563eb">Demande d'essai à examiner</h2>
              <p><strong>{{ institution_name | html.escape }}</strong> demande un essai gratuit du CRM socle.</p>
              <table style="border-collapse:collapse;width:100%;margin:16px 0">
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Demandeur</td><td style="padding:8px;border:1px solid #eee">{{ full_name | html.escape }}</td></tr>
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">E-mail</td><td style="padding:8px;border:1px solid #eee">{{ email | html.escape }}</td></tr>
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Téléphone</td><td style="padding:8px;border:1px solid #eee">{{ phone | html.escape }}</td></tr>
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Pays</td><td style="padding:8px;border:1px solid #eee">{{ country | html.escape }}</td></tr>
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Activité</td><td style="padding:8px;border:1px solid #eee">{{ activity_type | html.escape }}</td></tr>
                <tr><td style="padding:8px;border:1px solid #eee;font-weight:600;width:35%">Adresse demandée</td><td style="padding:8px;border:1px solid #eee">{{ crm_address | html.escape }}</td></tr>
              </table>
              <p>L'adresse e-mail est confirmée. La demande attend votre décision dans l'application d'administration, rubrique « Demandes d'essai ».</p>
            """, ct);

        // ── To the visitor: refused ──
        // Variables: first_name, institution_name, reason.
        await SeedOneAsync(db, logger, "trial.signup-rejected", "fr",
            "Votre demande d'essai du CRM",
            """
              <h2 style="color:#d97706">Demande non retenue</h2>
              <p>Bonjour {{ first_name | html.escape }},</p>
              <p>Nous ne pouvons pas donner suite à la demande d'essai gratuit du CRM pour
                 <strong>{{ institution_name | html.escape }}</strong>.</p>
              <p><strong>Motif :</strong> {{ reason | html.escape }}</p>
              <p>Pour en parler, répondez simplement à ce message.</p>
            """, ct);
        await SeedOneAsync(db, logger, "trial.signup-rejected", "en",
            "Your CRM trial request",
            """
              <h2 style="color:#d97706">Request not accepted</h2>
              <p>Hello {{ first_name | html.escape }},</p>
              <p>We cannot follow up on the free CRM trial request for
                 <strong>{{ institution_name | html.escape }}</strong>.</p>
              <p><strong>Reason:</strong> {{ reason | html.escape }}</p>
              <p>To talk about it, simply reply to this message.</p>
            """, ct);

        // ── To the visitor: the CRM is ready ──
        // Variables: first_name, institution_name, crm_url, activation_url (empty once signed in),
        // email, trial_end, expires_days.
        await SeedOneAsync(db, logger, "trial.ready", "fr",
            "Votre CRM est prêt",
            """
              <h2 style="color:#16a34a">Votre CRM est prêt</h2>
              <p>Bonjour {{ first_name | html.escape }},</p>
              <p>Le CRM de <strong>{{ institution_name | html.escape }}</strong> est ouvert à l'adresse
                 <a href="{{ crm_url }}">{{ crm_url | html.escape }}</a>. Votre essai gratuit dure jusqu'au <strong>{{ trial_end }}</strong>.</p>
              {{ if activation_url != "" }}
              <p>Choisissez votre mot de passe pour vous connecter avec <strong>{{ email | html.escape }}</strong> :</p>
            """ + string.Format(Button, "{{ activation_url }}", "Choisir mon mot de passe",
                "Le lien est valable {{ expires_days }} jours. Ensuite, utilisez « Mot de passe oublié » sur la page de connexion. Si le bouton ne fonctionne pas, copiez ce lien :") + """
              {{ else }}
              <p>Connectez-vous avec <strong>{{ email | html.escape }}</strong>.</p>
              {{ end }}
              <p>Vous trouverez déjà une agence, quelques produits et vos sources de leads pour démarrer. Pour toute question, répondez simplement à ce message.</p>
            """, ct);
        await SeedOneAsync(db, logger, "trial.ready", "en",
            "Your CRM is ready",
            """
              <h2 style="color:#16a34a">Your CRM is ready</h2>
              <p>Hello {{ first_name | html.escape }},</p>
              <p>The CRM of <strong>{{ institution_name | html.escape }}</strong> is open at
                 <a href="{{ crm_url }}">{{ crm_url | html.escape }}</a>. Your free trial runs until <strong>{{ trial_end }}</strong>.</p>
              {{ if activation_url != "" }}
              <p>Choose your password to sign in with <strong>{{ email | html.escape }}</strong>:</p>
            """ + string.Format(Button, "{{ activation_url }}", "Choose my password",
                "The link is valid for {{ expires_days }} days. After that, use « Forgot password » on the sign-in page. If the button does not work, copy this link:") + """
              {{ else }}
              <p>Sign in with <strong>{{ email | html.escape }}</strong>.</p>
              {{ end }}
              <p>An agency, a few products and your lead sources are already there to get you started. For any question, simply reply to this message.</p>
            """, ct);
    }
}

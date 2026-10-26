namespace Sankore.Modules.Notifications.Infrastructure;

using Microsoft.Extensions.Logging;

/// <summary>
/// Insurance lot 2: the distributor pieces near expiry (to the compliance administrators) and the
/// premiums still owed on a contract sent without them (to the agent who subscribed it).
/// </summary>
internal static partial class NotificationsSeeder
{
    private static async Task SeedInsuranceDistributionTemplatesAsync(NotificationsDbContext db, ILogger logger, CancellationToken ct)
    {
        // Variables: document_label, reference, expires_on, days_left.
        await SeedOneAsync(db, logger, "insurance.distributor-document-expiring", "fr",
            "{{ if days_left > 0 }}{{ document_label }} : échéance dans {{ days_left }} jours{{ else }}{{ document_label }} : échu aujourd'hui{{ end }}",
            """
              <h2 style="color:#d97706">Pièce de distribution d'assurance à renouveler</h2>
              <p><strong>{{ document_label | html.escape }}</strong> n° {{ reference | html.escape }} expire le
                 <strong>{{ expires_on }}</strong>{{ if days_left > 0 }}, dans {{ days_left }} jours{{ end }}.</p>
              <p>Une fois échue, les nouvelles souscriptions d'assurance sont refusées tant que la pièce renouvelée
                 n'est pas enregistrée (Paramétrage › Distribution d'assurance). Les contrats en cours ne sont pas touchés.</p>
            """, ct);
        await SeedOneAsync(db, logger, "insurance.distributor-document-expiring", "en",
            "{{ if days_left > 0 }}{{ document_label }}: expires in {{ days_left }} days{{ else }}{{ document_label }}: expires today{{ end }}",
            """
              <h2 style="color:#d97706">Insurance distribution document to renew</h2>
              <p><strong>{{ document_label | html.escape }}</strong> no. {{ reference | html.escape }} expires on
                 <strong>{{ expires_on }}</strong>{{ if days_left > 0 }}, in {{ days_left }} days{{ end }}.</p>
              <p>Once expired, new insurance subscriptions are refused until the renewed document is recorded
                 (Settings › Insurance distribution). Existing contracts are not affected.</p>
            """, ct);

        // Variables: client_name, policy_reference, amount, due_on, days_left.
        await SeedOneAsync(db, logger, "insurance.premium-payment-due", "fr",
            "{{ if days_left > 0 }}Prime à encaisser avant le {{ due_on }}{{ else }}Prime non encaissée au {{ due_on }}{{ end }}",
            """
              <h2 style="color:#d97706">Prime d'assurance en attente de paiement</h2>
              <p>La prime de <strong>{{ amount | html.escape }}</strong> du contrat de {{ client_name | html.escape }}
                 {{ if policy_reference != "" }}(police n° {{ policy_reference | html.escape }}){{ end }} n'est pas encore encaissée.</p>
              <p>{{ if days_left > 0 }}Elle doit l'être avant le <strong>{{ due_on }}</strong>, dans {{ days_left }} jours.{{ else }}L'échéance du <strong>{{ due_on }}</strong> est atteinte : l'assureur peut suspendre puis résilier la garantie.{{ end }}</p>
            """, ct);
        await SeedOneAsync(db, logger, "insurance.premium-payment-due", "en",
            "{{ if days_left > 0 }}Premium to collect before {{ due_on }}{{ else }}Premium not collected by {{ due_on }}{{ end }}",
            """
              <h2 style="color:#d97706">Insurance premium awaiting payment</h2>
              <p>The premium of <strong>{{ amount | html.escape }}</strong> on {{ client_name | html.escape }}'s contract
                 {{ if policy_reference != "" }}(policy no. {{ policy_reference | html.escape }}){{ end }} has not been collected yet.</p>
              <p>{{ if days_left > 0 }}It is due before <strong>{{ due_on }}</strong>, in {{ days_left }} days.{{ else }}The deadline of <strong>{{ due_on }}</strong> is reached: the insurer may suspend then cancel the cover.{{ end }}</p>
            """, ct);

        // Variables: title, message — the follow-ups of lots 3-4 (insurer's 15 days, renunciation
        // refunds, remittances), written by InsuranceFollowUpJob.
        await SeedOneAsync(db, logger, "insurance.follow-up", "fr", "{{ title }}",
            """
              <h2 style="color:#d97706">{{ title | html.escape }}</h2>
              <p>{{ message | html.escape }}</p>
            """, ct);
        await SeedOneAsync(db, logger, "insurance.follow-up", "en", "{{ title }}",
            """
              <h2 style="color:#d97706">{{ title | html.escape }}</h2>
              <p>{{ message | html.escape }}</p>
            """, ct);
    }
}

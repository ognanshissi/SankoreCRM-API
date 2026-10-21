namespace Sankore.Modules.Leads.Features.Ingestion;

using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// The WhatsApp opt-in a prospect gave on the form that captured them (WA-BE-05), recorded as a
/// <see cref="LeadConsent"/> of type <see cref="ConsentType.WhatsAppContact"/>.
///
/// <para>
/// <b>Where it is read from.</b> An embedded-script source (SDK ≥ 1.3.0) posts its box, which the
/// ingest endpoint files under <see cref="EmbeddedScriptKey"/>. A webhook or pulled source names
/// the field in <c>ConsentConfig.WhatsAppConsentFieldPath</c>. Nothing configured, nothing read:
/// a form that never asked records no consent — neither an agreement nor a refusal.
/// </para>
///
/// <para>
/// <b>Only an agreement is recorded.</b> An unticked box is the default state of an optional
/// question, not a « non » the prospect stated; recording it as a refusal would make an agent's
/// later, real consent look like a contradiction. And an agreement is recorded only when the lead
/// holds no ACTIVE one — a form submitted again after a withdrawal IS a new consent with its own
/// proof, which is exactly what a withdrawal requires to be lifted.
/// </para>
/// </summary>
internal static class InboundWhatsAppConsent
{
    /// <summary>The reserved key the ingest endpoint files the SDK's box under.</summary>
    public const string EmbeddedScriptKey = "_whatsappConsent";

    /// <summary>
    /// True when the payload says the prospect accepted, false when it says they did not, null
    /// when the source carries no WhatsApp question or the value cannot be read.
    /// </summary>
    public static bool? Read(LeadSourceConfig source, string rawPayloadJson)
    {
        var path = source.Settings?.Consent?.WhatsAppConsentFieldPath is { Length: > 0 } configured
            ? configured.Trim()
            : source.Mode == IntegrationMode.EmbeddedScript ? EmbeddedScriptKey : null;

        if (path is null || string.IsNullOrWhiteSpace(rawPayloadJson)) return null;

        JToken? token;

        try
        {
            var payload = JObject.Parse(rawPayloadJson);
            token = path.StartsWith('$') ? payload.SelectToken(path) : payload[path];
        }
        catch (Exception)
        {
            // A malformed payload or path is "not readable", never a consent.
            return null;
        }

        if (token is null || token.Type == JTokenType.Null) return null;

        if (token.Type == JTokenType.Boolean) return token.Value<bool>();

        return token.ToString().Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "oui" or "on" or "y" or "o" => true,
            "false" or "0" or "no" or "non" or "off" or "n" => false,
            _ => null,
        };
    }

    /// <summary>
    /// Records the agreement on <paramref name="lead"/> when the payload carries one and the lead
    /// has no active WhatsApp consent. Adds to the context; the caller saves.
    /// </summary>
    public static async Task RecordIfGivenAsync(
        LeadsDbContext db,
        LeadSourceConfig source,
        string rawPayloadJson,
        Lead lead,
        Guid ingestionId,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (Read(source, rawPayloadJson) != true) return;

        var alreadyActive = await db.LeadConsents
                                .IgnoreQueryFilters()
                                .AnyAsync(c => c.TenantId == lead.TenantId
                                            && c.LeadId == lead.Id
                                            && c.Type == ConsentType.WhatsAppContact
                                            && c.SubjectId == null
                                            && c.Status == ConsentStatus.Active, ct)
                            || db.LeadConsents.Local.Any(c => c.LeadId == lead.Id
                                                           && c.Type == ConsentType.WhatsAppContact
                                                           && c.Status == ConsentStatus.Active);

        if (alreadyActive) return;

        var version = ReadConsentVersion(rawPayloadJson);

        var consent = LeadConsent.Create(
            tenantId: lead.TenantId,
            leadId: lead.Id,
            type: ConsentType.WhatsAppContact,
            channel: ConsentChannel.WebForm,
            // Nobody recorded it: the prospect ticked the box. Never an agent's id.
            recordedBy: Guid.Empty,
            clock: clock,
            proofReference: $"Source {source.Code} · ingestion {ingestionId}"
                            + (version is null ? string.Empty : $" · texte {version}"));

        db.LeadConsents.Add(consent);
        lead.RaiseConsentRecordedEvent(consent.Id, ConsentType.WhatsAppContact.ToString());
    }

    private static string? ReadConsentVersion(string rawPayloadJson)
    {
        try
        {
            var value = JObject.Parse(rawPayloadJson)["_consentVersion"]?.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, 50)];
        }
        catch (Exception)
        {
            return null;
        }
    }
}

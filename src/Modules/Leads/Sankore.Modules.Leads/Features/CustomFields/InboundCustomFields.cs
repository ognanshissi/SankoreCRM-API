namespace Sankore.Modules.Leads.Features.CustomFields;

using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Modules.Leads.Domain;

/// <summary>
/// Custom values carried by an inbound payload — a mapping rule targeting <c>custom.&lt;key&gt;</c>,
/// from a web form, the SDK's hosted form, a webhook or a scheduled pull — applied to the lead being
/// created.
///
/// <para>
/// <b>Best effort, never a refusal.</b> Nobody on the other end of a web form can correct a value,
/// and losing a prospect to a malformed optional field is the outcome every inbound path avoids.
/// The values the form accepts are stored; the others are logged with their keys and dropped.
/// Required custom fields are not enforced here, for the same reason they are not on an import.
/// </para>
/// </summary>
internal static class InboundCustomFields
{
    public static async Task ApplyAsync(
        IAdministrationModule? administration,
        ILogger? logger,
        Lead lead,
        IReadOnlyDictionary<string, string?> mapped,
        Guid sourceId,
        CancellationToken ct,
        bool mergeWithExisting = false)
    {
        var values = CustomFieldValues.FromText(mapped);
        if (values.Count == 0) return;

        if (administration is null)
        {
            logger?.LogWarning("Custom values of source {SourceId} dropped: no form service", sourceId);
            return;
        }

        var (json, rejected) = await EntityFormGate.ApplyBestEffortAsync(
            // An update by external id (webhooks lot 5) merges into what the lead holds; a capture
            // starts from nothing.
            administration, FormEntity.Lead, lead.TenantId, mergeWithExisting ? lead.CustomFieldsJson : null, values,
            new Dictionary<string, string?> { ["prospectType"] = lead.ProspectType.ToString() },
            ex => logger?.LogWarning(ex, "Lead form unreadable: custom values of source {SourceId} dropped", sourceId),
            ct);

        if (json is not null) lead.SetCustomFields(json);

        if (rejected.Count > 0)
        {
            logger?.LogWarning(
                "Lead {LeadId} from source {SourceId}: custom values dropped for {Keys}",
                lead.Id, sourceId, string.Join(", ", rejected));
        }
    }
}

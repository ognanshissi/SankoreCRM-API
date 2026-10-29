using System.Text.Json;
using Sankore.Modules.Workflow.Domain.Events;
using Sankore.Modules.Workflow.PublicApi;

namespace Sankore.Modules.Workflow.Infrastructure.Notifications;

/// <summary>
/// Reads a « Notification » step's configuration — the JSON the journey builder writes:
/// <c>{ "channel": "Email", "recipients": ["subject", "role"], "roleCodes": ["BranchManager"],
/// "subject": "…", "message": "…" }</c>.
///
/// <para>
/// A step saved before recipients existed only carries <c>channel</c>: whoever chose « Email » or
/// « SMS » meant the person of the record, so an ABSENT list reads as <c>subject</c>. An explicit
/// empty list means nobody.
/// </para>
/// </summary>
internal static class NotificationStepConfig
{
    public static WorkflowNotificationRequested ToRequest(NotificationStepExecutedEvent executed)
    {
        var settings = Read(executed.ConfigJson);
        return new WorkflowNotificationRequested(
            executed.TenantId,
            executed.InstanceId,
            executed.InstanceStepId,
            executed.StepName,
            executed.EntityType,
            executed.EntityId,
            settings.Channel,
            settings.Recipients,
            settings.RoleCodes,
            settings.Subject,
            settings.Message);
    }

    /// <summary>
    /// Why this step would send nothing, in French for the screen that activates the circuit —
    /// or null. The journey builder checks the same before publishing; the « Activer » button of
    /// the trigger tab does not, hence this.
    /// </summary>
    public static string? Problem(string stepName, string? configJson)
    {
        var settings = Read(configJson);
        if (settings.Recipients.Count == 0)
            return $"L'étape « {stepName} » n'a pas de destinataire.";
        if (settings.Recipients.Contains(WorkflowNotificationRecipients.Role, StringComparer.OrdinalIgnoreCase)
            && settings.RoleCodes.Count == 0)
            return $"L'étape « {stepName} » est adressée à un rôle sans en nommer aucun.";
        if (settings.Message is null)
            return $"L'étape « {stepName} » n'a pas de message.";
        return null;
    }

    public static Settings Read(string? configJson)
    {
        var channel = "Email";
        IReadOnlyList<string> recipients = [WorkflowNotificationRecipients.Subject];
        IReadOnlyList<string> roleCodes = [];
        string? subject = null, message = null;

        if (!string.IsNullOrWhiteSpace(configJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(configJson);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (Text(root, "channel") is { } c && c.Equals("Sms", StringComparison.OrdinalIgnoreCase))
                        channel = "Sms";
                    if (root.TryGetProperty("recipients", out var r) && r.ValueKind == JsonValueKind.Array)
                        recipients = Strings(r);
                    if (root.TryGetProperty("roleCodes", out var rc) && rc.ValueKind == JsonValueKind.Array)
                        roleCodes = Strings(rc);
                    subject = Text(root, "subject");
                    message = Text(root, "message");
                }
            }
            catch (JsonException)
            {
                // An unreadable configuration still produces a request: the owner module records
                // « nothing to send » against the step instead of the request vanishing here.
            }
        }

        return new Settings(channel, recipients, roleCodes, subject, message);
    }

    public sealed record Settings(
        string Channel,
        IReadOnlyList<string> Recipients,
        IReadOnlyList<string> RoleCodes,
        string? Subject,
        string? Message);

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static IReadOnlyList<string> Strings(JsonElement array)
        => array.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
            .Select(e => e.GetString()!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

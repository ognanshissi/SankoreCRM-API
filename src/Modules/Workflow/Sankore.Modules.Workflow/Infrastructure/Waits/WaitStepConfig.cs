using System.Text.Json;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Infrastructure.Waits;

/// <summary>
/// An « Attente » step: a delay (the step's <c>TimeoutHours</c>, which makes its <c>DueAt</c>) and/or
/// an internal event it waits for (<c>{ "eventCode": "lead.qualified" }</c> in its configuration).
/// With both, the delay is a maximum: past it the circuit resumes without the event.
/// </summary>
internal static class WaitStepConfig
{
    public static string? EventCode(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("eventCode", out var code)
                && code.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(code.GetString())
                    ? code.GetString()!.Trim()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Why this wait could never end, in French for the activation screen — or null.</summary>
    public static string? Problem(string stepName, int? timeoutHours, string? configJson, string templateEntityType)
    {
        var code = EventCode(configJson);
        if (code is null)
            return timeoutHours is > 0 ? null : $"L'étape « {stepName} » n'attend ni délai ni événement.";

        var known = PlatformEvents.Find(code);
        if (known is null || !known.StartsWorkflows)
            return $"L'étape « {stepName} » attend un événement inconnu ({code}).";
        if (!string.Equals(known.WorkflowEntityType, templateEntityType, StringComparison.OrdinalIgnoreCase))
            return $"L'étape « {stepName} » attend « {known.Label} », qui ne porte pas sur ce type de dossier.";
        return null;
    }

    /// <summary>The history line when the delay runs out.</summary>
    public static string ElapsedReason(string? configJson)
        => EventCode(configJson) is { } code
            ? $"Délai écoulé sans l'événement « {Label(code)} » : le parcours reprend."
            : "Délai d'attente écoulé : le parcours reprend.";

    public static string ReceivedReason(string code) => $"Événement reçu : « {Label(code)} ».";

    private static string Label(string code) => PlatformEvents.Find(code)?.Label ?? code;
}

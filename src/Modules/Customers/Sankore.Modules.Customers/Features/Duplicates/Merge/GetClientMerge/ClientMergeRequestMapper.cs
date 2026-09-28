namespace Sankore.Modules.Customers.Features.Duplicates.Merge.GetClientMerge;

using System.Text.Json;
using Sankore.Modules.Customers.Domain;

/// <summary>
/// Shared projection of a merge request, used by both the detail and the list endpoint so the two
/// can never drift apart.
/// </summary>
internal static class ClientMergeRequestMapper
{
    internal static ClientMergeRequestDto ToDto(ClientMergeRequest request, Client? survivor, Client? absorbed) =>
        new(request.Id,
            request.Status.ToString(),
            request.SurvivorClientId,
            survivor?.ClientNumber ?? string.Empty,
            survivor?.DisplayName ?? string.Empty,
            request.AbsorbedClientId,
            absorbed?.ClientNumber ?? string.Empty,
            absorbed?.DisplayName ?? string.Empty,
            ParseFieldChoices(request.FieldChoicesJson),
            request.WorkflowInstanceId,
            request.RequestedBy,
            request.RequestedAt,
            request.DecidedBy,
            request.DecidedAt,
            request.DecisionComment,
            request.ExecutedAt);

    /// <summary>
    /// The choices are stored as jsonb. A row that cannot be parsed must not take the screen down,
    /// so a malformed payload reads as "no explicit choice" — which is also the executor's default.
    /// </summary>
    private static Dictionary<string, string> ParseFieldChoices(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}

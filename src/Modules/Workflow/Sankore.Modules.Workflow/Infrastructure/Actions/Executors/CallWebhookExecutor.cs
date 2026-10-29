using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Workflow.Domain;
using Sankore.Shared.Infrastructure.Egress;

namespace Sankore.Modules.Workflow.Infrastructure.Actions.Executors;

/// <summary>
/// Calls an external HTTP endpoint when a transition fires.
/// Config shape: <c>{ "url": "https://...", "method": "POST", "headers": { "X-Api-Key": "..." } }</c>
/// The request body is a JSON-serialised <see cref="WorkflowContext"/> subset.
///
/// <para>
/// The URL is a tenant administrator's and the network position is ours, so the client is
/// <see cref="SafeOutboundHttp"/>'s. It used to be a plain <c>AddHttpClient</c>: a template
/// pointing at <c>http://169.254.169.254/</c> or <c>http://sankore-admin:8080/</c> was called from
/// inside the platform's network on every transition.
/// </para>
/// </summary>
internal sealed class CallWebhookExecutor(
    IHttpClientFactory httpClientFactory,
    IOptions<OutboundHttpOptions> egress,
    ILogger<CallWebhookExecutor> logger) : IActionExecutor
{
    public const string HttpClientName = "WorkflowWebhook";

    public ActionType ActionType => ActionType.CallWebhook;

    public async Task ExecuteAsync(WorkflowAction action, WorkflowContext context, CancellationToken ct)
    {
        var cfg = CallWebhookConfig.Parse(action.ConfigJson);
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.Url))
            return;

        // Re-checked here and not only at save: actions saved before the check existed are
        // still in the database.
        var problem = cfg.Validate(egress.Value);
        if (problem is not null)
        {
            logger.LogWarning(
                "Webhook action {ActionId} skipped: {Problem}", action.Id, problem);
            return;
        }

        var client = httpClientFactory.CreateClient(HttpClientName);

        var body = JsonSerializer.Serialize(new
        {
            instanceId = context.InstanceId,
            tenantId   = context.TenantId,
            entityType = context.EntityType,
            entityId   = context.EntityId,
            actedBy    = context.ActedByUserId,
            occurredAt = DateTimeOffset.UtcNow
        });

        using var request = new HttpRequestMessage(new HttpMethod(cfg.Method), cfg.Url.Trim())
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        foreach (var (key, value) in cfg.Headers)
            request.Headers.TryAddWithoutValidation(key, value);

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// The configuration of a <see cref="ActionType.CallWebhook"/> action, read by the executor and by
/// <c>AddActionValidator</c> so a refused configuration is refused when it is saved, not on the
/// first transition nobody is watching.
/// </summary>
internal sealed class CallWebhookConfig
{
    private static readonly string[] AllowedMethods = ["POST", "PUT", "PATCH"];

    /// <summary>
    /// Headers the transport owns. <c>Host</c> would re-aim a request at a virtual host the URL
    /// never named; the content and framing headers would contradict the body we send.
    /// </summary>
    private static readonly string[] ReservedHeaders =
    [
        "Host", "Content-Length", "Content-Type", "Content-Encoding", "Transfer-Encoding",
        "Connection", "Upgrade", "Expect", "TE", "Trailer", "Proxy-Authorization", "Proxy-Connection",
    ];

    public string Url { get; init; } = string.Empty;
    public string Method { get; init; } = "POST";
    public Dictionary<string, string> Headers { get; init; } = [];

    public static CallWebhookConfig? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<CallWebhookConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The reason this configuration may not be used, or null.</summary>
    public string? Validate(OutboundHttpOptions? egress = null)
    {
        var urlProblem = SafeOutboundHttp.ValidateUrl(Url, egress);
        if (urlProblem is not null) return urlProblem;

        if (!AllowedMethods.Contains(Method?.ToUpperInvariant()))
            return $"Méthode non autorisée ({Method}). Méthodes acceptées : {string.Join(", ", AllowedMethods)}.";

        foreach (var (key, value) in Headers ?? [])
        {
            if (string.IsNullOrWhiteSpace(key) || key.Any(c => c <= ' ' || c == ':' || c > '~'))
                return $"Nom d’en-tête invalide : « {key} ».";

            if (ReservedHeaders.Contains(key, StringComparer.OrdinalIgnoreCase))
                return $"L’en-tête « {key} » est géré par la plateforme et ne peut pas être défini.";

            if (value is null || value.Contains('\r') || value.Contains('\n'))
                return $"Valeur d’en-tête invalide pour « {key} ».";
        }

        return null;
    }
}

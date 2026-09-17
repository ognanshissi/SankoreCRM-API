using System.Text;
using System.Text.Json;
using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Infrastructure.Actions.Executors;

/// <summary>
/// Calls an external HTTP endpoint when a transition fires.
/// Config shape: <c>{ "url": "https://...", "method": "POST", "headers": { "X-Api-Key": "..." } }</c>
/// The request body is a JSON-serialised <see cref="WorkflowContext"/> subset.
/// </summary>
internal sealed class CallWebhookExecutor(IHttpClientFactory httpClientFactory) : IActionExecutor
{
    public ActionType ActionType => ActionType.CallWebhook;

    public async Task ExecuteAsync(WorkflowAction action, WorkflowContext context, CancellationToken ct)
    {
        var cfg = JsonSerializer.Deserialize<WebhookConfig>(action.ConfigJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new WebhookConfig();

        if (string.IsNullOrWhiteSpace(cfg.Url))
            return;

        var client = httpClientFactory.CreateClient("WorkflowWebhook");

        var body = JsonSerializer.Serialize(new
        {
            instanceId = context.InstanceId,
            tenantId   = context.TenantId,
            entityType = context.EntityType,
            entityId   = context.EntityId,
            actedBy    = context.ActedByUserId,
            occurredAt = DateTimeOffset.UtcNow
        });

        using var request = new HttpRequestMessage(
            new HttpMethod(cfg.Method),
            cfg.Url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        foreach (var (key, value) in cfg.Headers)
            request.Headers.TryAddWithoutValidation(key, value);

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private sealed class WebhookConfig
    {
        public string Url { get; init; } = string.Empty;
        public string Method { get; init; } = "POST";
        public Dictionary<string, string> Headers { get; init; } = [];
    }
}

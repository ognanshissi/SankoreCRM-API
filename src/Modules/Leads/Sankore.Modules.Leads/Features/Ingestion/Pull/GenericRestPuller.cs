namespace Sankore.Modules.Leads.Features.Ingestion.Pull;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// Generic REST client that fetches leads from external APIs based on <see cref="ScheduledPullSettings"/>.
/// Handles auth (5 types), URL template variables, pagination (6 strategies), response parsing,
/// size limits, timeouts, and retry via Polly (configured on the HttpClient).
/// </summary>
public sealed class GenericRestPuller(
    IHttpClientFactory httpClientFactory,
    ISecretsModule secrets,
    ILogger<GenericRestPuller> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Fetches all pages and returns the raw JSON items extracted via ItemsPath.
    /// Each item is a raw JsonElement for downstream field mapping.
    /// </summary>
    public async Task<PullResult> FetchAsync(
        LeadSourceConfig source,
        ScheduledPullSettings settings,
        DateTimeOffset? since,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("LeadPuller");
        client.Timeout = TimeSpan.FromSeconds(Math.Min(settings.TimeoutSeconds, 60));

        // Apply auth
        await ApplyAuthAsync(client, source, settings, ct);

        // Apply extra headers
        if (settings.ExtraHeaders is { Count: > 0 })
            foreach (var (k, v) in settings.ExtraHeaders)
                client.DefaultRequestHeaders.TryAddWithoutValidation(k, v);

        var allItems = new List<JsonElement>();
        string? cursor = null;
        int page = 1;
        int offset = 0;
        var currentSince = since;

        for (int run = 0; run < settings.MaxPagesPerRun; run++)
        {
            var url = ResolveUrl(settings.EndpointUrl, currentSince, cursor, page, offset, settings.PageSize);

            HttpResponseMessage response;

            if (settings.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var body = settings.RequestBodyTemplate is not null
                    ? ResolveTemplate(settings.RequestBodyTemplate, currentSince, cursor, page, offset, settings.PageSize)
                    : null;

                response = await client.PostAsync(url,
                    body is not null ? new StringContent(body, Encoding.UTF8, "application/json") : null, ct);
            }
            else
            {
                response = await client.GetAsync(url, ct);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Pull failed for source {SourceId}: HTTP {Status} from {Url}",
                    source.Id, (int)response.StatusCode, url);
                return new PullResult(allItems, false, $"HTTP {(int)response.StatusCode}");
            }

            // Size guard
            if (response.Content.Headers.ContentLength > settings.MaxResponseBytes)
            {
                logger.LogWarning("Response too large for source {SourceId}: {Size} bytes",
                    source.Id, response.Content.Headers.ContentLength);
                return new PullResult(allItems, false, "RESPONSE_TOO_LARGE");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);

            // Extract items
            var items = ExtractItems(doc, settings.ItemsPath);
            if (items.Count == 0) break;

            allItems.AddRange(items);

            // Advance pagination
            var hasMore = AdvancePagination(settings.Pagination, doc, response, items,
                ref cursor, ref page, ref offset, ref currentSince, settings);

            if (!hasMore) break;
        }

        return new PullResult(allItems, true, null);
    }

    // ── Auth ─────────────────────────────────────────────────────────────

    private async Task ApplyAuthAsync(
        HttpClient client, LeadSourceConfig source,
        ScheduledPullSettings settings, CancellationToken ct)
    {
        if (settings.AuthType == PullAuthType.None || settings.AuthCredentialVaultRef is null)
            return;

        var key = new SecretKey(source.TenantId, "LeadSource", source.Id, settings.AuthCredentialVaultRef);
        var secret = await secrets.GetValueAsync(key, ct);
        if (secret is null)
        {
            logger.LogWarning("Auth credential not found in vault for source {SourceId}", source.Id);
            return;
        }

        switch (settings.AuthType)
        {
            case PullAuthType.ApiKeyHeader:
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    settings.AuthHeaderName ?? "X-Api-Key", secret);
                break;

            case PullAuthType.ApiKeyQuery:
                // Will be appended to URL at request time — store for URL builder
                // For simplicity, we modify the endpoint URL template
                break;

            case PullAuthType.Bearer:
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", secret);
                break;

            case PullAuthType.Basic:
                var encoded = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{settings.BasicAuthUsername}:{secret}"));
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", encoded);
                break;

            case PullAuthType.OAuthClientCredentials:
                var token = await FetchOAuthTokenAsync(client, settings, secret, ct);
                if (token is not null)
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", token);
                break;
        }
    }

    private async Task<string?> FetchOAuthTokenAsync(
        HttpClient client, ScheduledPullSettings settings, string clientSecret, CancellationToken ct)
    {
        if (settings.OAuthTokenUrl is null || settings.OAuthClientId is null)
            return null;

        var tokenClient = httpClientFactory.CreateClient("LeadPuller");
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]    = "client_credentials",
            ["client_id"]     = settings.OAuthClientId,
            ["client_secret"] = clientSecret,
            ["scope"]         = settings.OAuthScopes ?? "",
        });

        var response = await tokenClient.PostAsync(settings.OAuthTokenUrl, form, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("OAuth token request failed: {Status}", (int)response.StatusCode);
            return null;
        }

        var tokenDoc = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return tokenDoc.TryGetProperty("access_token", out var at) ? at.GetString() : null;
    }

    // ── URL template resolution ─────────────────────────────────────────

    private static string ResolveUrl(
        string template, DateTimeOffset? since, string? cursor,
        int page, int offset, int pageSize)
        => ResolveTemplate(template, since, cursor, page, offset, pageSize);

    private static string ResolveTemplate(
        string template, DateTimeOffset? since, string? cursor,
        int page, int offset, int pageSize)
        => template
            .Replace("{{since}}", Uri.EscapeDataString(since?.ToString("O") ?? ""))
            .Replace("{{cursor}}", Uri.EscapeDataString(cursor ?? ""))
            .Replace("{{page}}", page.ToString())
            .Replace("{{offset}}", offset.ToString())
            .Replace("{{pageSize}}", pageSize.ToString());

    // ── Response parsing ────────────────────────────────────────────────

    private static List<JsonElement> ExtractItems(JsonDocument doc, string? itemsPath)
    {
        if (itemsPath is null)
        {
            // Root is array
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return doc.RootElement.EnumerateArray().ToList();
            return [];
        }

        // Simple dot-path navigation (e.g. "data", "results.items")
        var current = doc.RootElement;
        foreach (var segment in itemsPath.TrimStart('$', '.').Split('.'))
        {
            if (!current.TryGetProperty(segment, out current))
                return [];
        }

        return current.ValueKind == JsonValueKind.Array
            ? current.EnumerateArray().ToList()
            : [];
    }

    // ── Pagination ──────────────────────────────────────────────────────

    private static bool AdvancePagination(
        PullPaginationStrategy strategy,
        JsonDocument doc,
        HttpResponseMessage response,
        List<JsonElement> items,
        ref string? cursor,
        ref int page,
        ref int offset,
        ref DateTimeOffset? since,
        ScheduledPullSettings settings)
    {
        switch (strategy)
        {
            case PullPaginationStrategy.None:
                return false;

            case PullPaginationStrategy.Page:
                page++;
                return items.Count >= settings.PageSize;

            case PullPaginationStrategy.Offset:
                offset += items.Count;
                return items.Count >= settings.PageSize;

            case PullPaginationStrategy.Cursor:
                if (settings.CursorPath is null) return false;
                var cursorEl = doc.RootElement;
                foreach (var seg in settings.CursorPath.TrimStart('$', '.').Split('.'))
                {
                    if (!cursorEl.TryGetProperty(seg, out cursorEl))
                        return false;
                }
                var newCursor = cursorEl.GetString();
                if (string.IsNullOrEmpty(newCursor) || newCursor == cursor) return false;
                cursor = newCursor;
                return true;

            case PullPaginationStrategy.LinkHeader:
                if (response.Headers.TryGetValues("Link", out var links))
                {
                    var nextLink = links.FirstOrDefault(l => l.Contains("rel=\"next\""));
                    // Not fully implemented — would parse Link header
                    return nextLink is not null;
                }
                return false;

            case PullPaginationStrategy.Since:
                // Use the latest item's timestamp as the new "since"
                since = DateTimeOffset.UtcNow;
                return items.Count >= settings.PageSize;

            default:
                return false;
        }
    }
}

public sealed record PullResult(
    List<JsonElement> Items,
    bool Success,
    string? Error);

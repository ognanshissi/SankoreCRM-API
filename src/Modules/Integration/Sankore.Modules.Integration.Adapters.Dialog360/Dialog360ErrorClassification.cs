namespace Sankore.Modules.Integration.Adapters.Dialog360;

using System.Net;
using System.Text.Json;
using Sankore.Modules.Integration.Features.Messaging;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Turns a Cloud API refusal into one of this module's <c>MESSAGING_*</c> codes (WA-BE-01,
/// criterion 3).
///
/// <para>
/// <b>Meta's codes, not 360dialog's</b> — the table itself is <see cref="CloudApiErrors"/>, shared
/// with the webhook path, because a refusal arrives either way: synchronously on the send, or
/// minutes later as a <c>failed</c> status. 360dialog forwards the Cloud API's error object
/// (<c>{"error":{"code":131047,…}}</c>) and, on its own proxy failures, a <c>meta</c> block with
/// an HTTP code. Every code the table does not know falls back to the HTTP status, which is never
/// wrong in the permissive direction: an unknown 4xx is a functional refusal, never a retry.
/// </para>
///
/// <para>
/// <b>Transient means "send it again later", and only two things are.</b> A throttle (Meta's
/// rate limits, an HTTP 429) and the provider being down. Everything else is an answer about this
/// message, and sending it again produces the same answer — or, for a template refusal, spends the
/// institution's quality rating on it.
/// </para>
/// </summary>
internal static class Dialog360ErrorClassification
{
    /// <summary>
    /// The result for a refusal. <paramref name="body"/> is read for its error code only — it is
    /// never logged or returned, because a refusal may quote the parameter it refused.
    /// </summary>
    internal static IntegrationResult Classify(HttpStatusCode status, string? body, string operation)
    {
        var (code, title) = ReadError(body);
        var detail = Detail(operation, status, code, title);

        if (code is { } c && CloudApiErrors.Translate(c) is { } known)
        {
            return known.Family switch
            {
                ErrorFamily.Transient => IntegrationResult.Transient(known.Code, detail),
                ErrorFamily.Technical => IntegrationResult.Technical(known.Code, detail),
                _ => IntegrationResult.Functional(known.Code, detail),
            };
        }

        return (int)status switch
        {
            429 => IntegrationResult.Transient(IntegrationErrors.MessagingQuotaExceeded, detail),
            401 or 403 => IntegrationResult.Technical(IntegrationErrors.AuthenticationRefused, detail),
            >= 500 => IntegrationResult.Transient(IntegrationErrors.Unavailable, detail),
            _ => IntegrationResult.Functional(IntegrationErrors.Rejected, detail),
        };
    }

    /// <summary>
    /// The numeric code and Meta's title, from either shape: the Cloud API's
    /// <c>{"error":{"code":…,"title"|"message":…}}</c>, or 360dialog's own
    /// <c>{"meta":{"http_code":…,"developer_message":…}}</c>.
    /// </summary>
    internal static (int? Code, string? Title) ReadError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return (null, null);

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                int? code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : null;

                var title = error.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString()
                        : null;

                return (code, title);
            }

            if (root.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() > 0
                && errors[0].ValueKind == JsonValueKind.Object)
            {
                var first = errors[0];
                int? code = first.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : null;
                var title = first.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : null;

                return (code, title);
            }

            if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                var title = meta.TryGetProperty("developer_message", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString()
                    : null;

                return (null, title);
            }
        }
        catch (JsonException)
        {
            // A proxy's HTML page. The status alone classifies it.
        }

        return (null, null);
    }

    private static string Detail(string operation, HttpStatusCode status, int? code, string? title)
    {
        var shortTitle = title is { Length: > 200 } ? title[..200] : title;

        return code is null
            ? $"{operation}: 360dialog answered HTTP {(int)status}{(shortTitle is null ? "" : $" ({shortTitle})")}."
            : $"{operation}: 360dialog answered HTTP {(int)status}, Meta code {code}{(shortTitle is null ? "" : $" ({shortTitle})")}.";
    }
}

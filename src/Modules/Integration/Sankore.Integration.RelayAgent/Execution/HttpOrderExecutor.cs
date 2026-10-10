namespace Sankore.Integration.RelayAgent.Execution;

using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Sankore.Integration.RelayAgent.Configuration;
using Sankore.Integration.RelayAgent.Observability;
using Sankore.Integration.RelayAgent.Protocol;

/// <summary>
/// Criterion 3, the local HTTP call.
///
/// <para>
/// <b>The address comes from the configuration file, never from the wire.</b> An order names a
/// declared target and supplies a relative path; the base URL, the headers and the budget are
/// the IMF's own. Three checks make that more than an intention:
/// </para>
/// <list type="number">
///   <item>the target name must be in the allow-list, or the order is refused unexecuted;</item>
///   <item>
///     the method must be one the target was declared to allow — so a target declared
///     <c>GET</c>-only cannot be written to, whatever SANKORE sends, which is how an IMF keeps a
///     read integration read-only without trusting us;
///   </item>
///   <item>
///     the resolved URI must still be under the base. An absolute URL, a leading slash or a
///     <c>..</c> would each otherwise reach a different address on the same network — and the
///     interesting addresses on a bank's internal network are all one hop away.
///   </item>
/// </list>
///
/// <para>
/// No header comes from the order either. A header the wire could set is an
/// <c>Authorization</c> the wire could set.
/// </para>
///
/// <para>
/// Note what is deliberately NOT here: SSRF filtering. The repository's <c>SsrfSafeHandler</c>
/// blocks RFC 1918 and loopback, and every address this executor is meant to reach is RFC 1918 —
/// a core banking system in a branch office. The protection that replaces it is the allow-list:
/// not "any address except the private ones" but "these addresses and no others".
/// </para>
/// </summary>
public sealed class HttpOrderExecutor : IRelayOrderExecutor
{
    /// <summary>Name of the <c>HttpClient</c> registered for relayed calls.</summary>
    public const string HttpClientName = "relay-local";

    private readonly IHttpClientFactory _clients;
    private readonly Dictionary<string, RelayHttpTargetOptions> _targets;

    public HttpOrderExecutor(IHttpClientFactory clients, IOptions<RelayAgentOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _clients = clients;
        _targets = options.Value.HttpTargets.ToDictionary(
            t => t.Name, StringComparer.OrdinalIgnoreCase);
    }

    public RelayOrderKind Kind => RelayOrderKind.HttpCall;

    public async Task<RelayExecution> ExecuteAsync(
        RelayOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (!_targets.TryGetValue(order.Target, out var target))
            return RelayExecution.Refuse(RelayErrorCodes.TargetNotDeclared);

        if (!OrderBody.TryRead<RelayHttpCallBody>(order, out var body) || body is null)
            return RelayExecution.Refuse(RelayErrorCodes.FrameInvalid);

        var method = body.Method?.ToUpperInvariant() ?? "GET";
        if (!target.AllowedMethods.Any(m => m.Equals(method, StringComparison.OrdinalIgnoreCase)))
            return RelayExecution.Refuse(RelayErrorCodes.MethodNotAllowed);

        if (!TryResolve(target.BaseUrl, body.Path, body.Query, out var uri) || uri is null)
            return RelayExecution.Refuse(RelayErrorCodes.PathOutsideBase);

        using var request = new HttpRequestMessage(new HttpMethod(method), uri);

        foreach (var (header, value) in target.Headers)
            request.Headers.TryAddWithoutValidation(header, value);

        if (body.Body is not null)
        {
            request.Content = new StringContent(
                body.Body, Encoding.UTF8, body.ContentType ?? "application/json");
        }

        // The budget is the target's, narrowed by the order if the order asks for less. An order
        // cannot ask for MORE: a relayed call that outlived the IMF's own declared budget would
        // hold an SSH-less but equally real resource — a connection into their network — for as
        // long as SANKORE felt like asking.
        var budget = TimeSpan.FromSeconds(
            order.TimeoutSeconds is > 0 and var requested && requested < target.TimeoutSeconds
                ? requested
                : target.TimeoutSeconds);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget);

        try
        {
            var client = _clients.CreateClient(HttpClientName);

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            await using var stream = await response.Content
                .ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);

            var (bytes, truncated) = await OrderBody
                .ReadCappedAsync(stream, target.MaxResponseBytes, timeout.Token)
                .ConfigureAwait(false);

            if (truncated)
            {
                // Reached, so the heartbeat must not mark it unreachable — the target answered,
                // we declined its answer.
                return RelayExecution.Unavailable(
                    RelayErrorCodes.PayloadTooLarge, reachedTarget: true);
            }

            return RelayExecution.Ok(Describe(response, bytes));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RelayExecution.Unavailable(
                RelayErrorCodes.TargetTimeout, nameof(OperationCanceledException));
        }
        catch (HttpRequestException ex)
        {
            // A connect failure and an answered 500 are both HttpRequestException territory, but
            // only the first means "unreachable" — the status code path never throws, so reaching
            // here with one is a transport failure.
            return RelayExecution.Unavailable(
                RelayErrorCodes.TargetUnreachable, ex.InnerException?.GetType().Name ?? nameof(HttpRequestException));
        }
        catch (SocketException ex)
        {
            return RelayExecution.Unavailable(RelayErrorCodes.TargetUnreachable, ex.GetType().Name);
        }
        catch (InvalidOperationException ex)
        {
            return RelayExecution.Unexpected(ex);
        }
    }

    /// <summary>
    /// Resolves the order's relative path against the declared base and proves the result is
    /// still inside it.
    ///
    /// <para>
    /// <c>Uri.IsBaseOf</c> after construction is what does the work: it compares the normalised
    /// forms, so <c>../../admin</c> has already collapsed by the time it is checked. The leading
    /// checks are there to refuse the same thing earlier and more legibly.
    /// </para>
    /// </summary>
    internal static bool TryResolve(
        string baseUrl,
        string? path,
        IReadOnlyDictionary<string, string>? query,
        out Uri? resolved)
    {
        resolved = null;

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)) return false;

        var relative = path ?? string.Empty;

        // Refused before resolution, because each would silently leave the base: an absolute URI
        // replaces it entirely, a leading slash replaces its path, and a scheme-relative "//host"
        // replaces its authority.
        if (relative.StartsWith('/') || relative.StartsWith('\\')) return false;
        if (Uri.TryCreate(relative, UriKind.Absolute, out _)) return false;
        if (relative.Contains('\\', StringComparison.Ordinal)) return false;

        // Two refusals that IsBaseOf below cannot make, because they depend on what the SERVER
        // does with the path rather than on what RFC 3986 says it means:
        //
        //   * an ENCODED separator (%2f, %5c). Uri normalises before decoding, so
        //     "..%2f..%2fadmin" stays a single harmless-looking segment and passes IsBaseOf — but
        //     a server that decodes first and normalises second lands two directories up. A
        //     declared local API never needs an encoded slash in a path, so refusing one costs
        //     nothing;
        //   * any ".." at all, as a substring and not as a segment. "....//admin" contains no
        //     ".." SEGMENT, so it too passes IsBaseOf, and a server that collapses it reaches
        //     "../admin". A legitimate REST path does not contain two consecutive dots.
        //
        // Both are the standard double-decoding bypass, and the version of this check that only
        // looked at the normalised URI let both through — a test pins each.
        if (relative.Contains("..", StringComparison.Ordinal)) return false;
        if (relative.Contains("%2f", StringComparison.OrdinalIgnoreCase)) return false;
        if (relative.Contains("%5c", StringComparison.OrdinalIgnoreCase)) return false;

        if (!Uri.TryCreate(baseUri, relative, out var candidate)) return false;
        if (!baseUri.IsBaseOf(candidate)) return false;

        // IsBaseOf compares scheme, authority and path prefix, so this is redundant — kept
        // because the authority is the check whose failure would matter most, and a reader should
        // not have to take IsBaseOf's word for it.
        if (!string.Equals(candidate.Authority, baseUri.Authority, StringComparison.OrdinalIgnoreCase))
            return false;

        if (query is { Count: > 0 })
        {
            var builder = new UriBuilder(candidate);
            var parts = query.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value ?? string.Empty)}");
            var encoded = string.Join('&', parts);

            builder.Query = string.IsNullOrEmpty(builder.Query)
                ? encoded
                : builder.Query.TrimStart('?') + '&' + encoded;

            candidate = builder.Uri;
        }

        resolved = candidate;
        return true;
    }

    /// <summary>
    /// Packs the answer for the wire. Textual content types travel as text so the platform can
    /// read them without a decode step; everything else travels as base64, because a PDF
    /// statement forced through UTF-8 arrives corrupted and the corruption is invisible until
    /// somebody opens it.
    /// </summary>
    private static JsonElement Describe(HttpResponseMessage response, byte[] bytes)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var textual = mediaType is not null
            && (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
                || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase));

        var payload = new RelayHttpResult(
            (int)response.StatusCode,
            mediaType,
            textual ? Encoding.UTF8.GetString(bytes) : null,
            textual ? null : Convert.ToBase64String(bytes));

        return JsonSerializer.SerializeToElement(payload, RelayProtocolJson.Options);
    }
}

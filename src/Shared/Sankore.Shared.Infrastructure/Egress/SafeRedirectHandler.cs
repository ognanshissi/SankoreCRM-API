namespace Sankore.Shared.Infrastructure.Egress;

using System.Net;
using Sankore.Shared.Kernel;

/// <summary>
/// Follows at most one redirect, to the same host and over HTTPS. A redirect is the classic way
/// round an address check: a public host answering 302 to <c>http://169.254.169.254/</c>. The
/// redirected request goes back through the guarded primary handler, so its address is checked
/// like the first one.
/// </summary>
public sealed class SafeRedirectHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);

        if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
            return response;

        var redirectUri = response.Headers.Location.IsAbsoluteUri
            ? response.Headers.Location
            : new Uri(request.RequestUri!, response.Headers.Location);

        if (!string.Equals(redirectUri.Host, request.RequestUri?.Host, StringComparison.OrdinalIgnoreCase)
            || redirectUri.Scheme != Uri.UriSchemeHttps)
        {
            response.Dispose();
            throw new DomainException(SafeOutboundHttp.RefusedCode);
        }

        // 307/308 keep the method and the body; 301/302 become a GET without a body, as browsers do.
        var keepsMethod = response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
        var redirectRequest = new HttpRequestMessage(keepsMethod ? request.Method : HttpMethod.Get, redirectUri)
        {
            Content = keepsMethod ? request.Content : null,
        };

        foreach (var header in request.Headers)
        {
            if (!header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                redirectRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Dispose();
        var second = await base.SendAsync(redirectRequest, ct);

        // One hop and no more: a second redirect is handed back as it is, never followed.
        return second;
    }

    private static bool IsRedirect(HttpStatusCode code)
        => code is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
}

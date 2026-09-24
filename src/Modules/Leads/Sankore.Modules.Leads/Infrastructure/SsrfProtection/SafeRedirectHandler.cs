namespace Sankore.Modules.Leads.Infrastructure.SsrfProtection;

using System.Net;
using Sankore.Shared.Kernel;

/// <summary>
/// DelegatingHandler that allows at most one redirect, and only to the same host.
/// Prevents redirect-based SSRF (e.g. external host redirects to 169.254.169.254).
/// </summary>
public sealed class SafeRedirectHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);

        // If redirect (301, 302, 307, 308), follow at most once and only same-host
        if (IsRedirect(response.StatusCode) && response.Headers.Location is not null)
        {
            var redirectUri = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(request.RequestUri!, response.Headers.Location);

            // Must be same host
            if (!string.Equals(redirectUri.Host, request.RequestUri?.Host, StringComparison.OrdinalIgnoreCase))
                throw new DomainException("OutboundAddressNotAllowed");

            // Must stay HTTPS
            if (redirectUri.Scheme != "https")
                throw new DomainException("OutboundAddressNotAllowed");

            // Follow once — no further redirects
            var redirectRequest = new HttpRequestMessage(request.Method, redirectUri);

            // Copy headers (except Host)
            foreach (var header in request.Headers)
            {
                if (!header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                    redirectRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Dispose();
            return await base.SendAsync(redirectRequest, ct);
        }

        return response;
    }

    private static bool IsRedirect(HttpStatusCode code)
        => code is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.TemporaryRedirect
            or (HttpStatusCode)308;
}

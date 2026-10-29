namespace Sankore.Shared.Infrastructure.Egress;

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sankore.Shared.Kernel;

/// <summary>
/// The HTTP client for a URL a tenant configured — a lead puller, a workflow webhook, an outbound
/// event subscription. Moved here from Leads (<c>SsrfSafeHandler</c>) so every module gets the
/// same guard instead of a plain <c>AddHttpClient</c>, which is what the workflow webhook had.
///
/// <list type="bullet">
///   <item>HTTPS only, on <see cref="OutboundHttpOptions.AllowedPorts"/> only.</item>
///   <item>The name is resolved HERE and the socket opened to the address that was checked, so a
///   DNS answer that changes between the check and the connect (rebinding) cannot slip past.</item>
///   <item>No automatic redirect; <see cref="SafeRedirectHandler"/> follows one, same host, HTTPS.</item>
///   <item>Every refusal is the same <see cref="RefusedCode"/>: three distinguishable failures
///   would let somebody map our internal network one address at a time.</item>
/// </list>
/// </summary>
public static class SafeOutboundHttp
{
    public const string RefusedCode = "OutboundAddressNotAllowed";

    /// <summary>
    /// Registers a named client behind the guard. Chain resilience or timeouts on the builder;
    /// never replace its primary handler.
    /// </summary>
    public static IHttpClientBuilder AddSafeOutboundHttpClient(this IServiceCollection services, string name)
    {
        services.AddOptions<OutboundHttpOptions>().BindConfiguration(OutboundHttpOptions.SectionName);
        services.TryAddTransient<SafeRedirectHandler>();

        return services.AddHttpClient(name)
            .ConfigurePrimaryHttpMessageHandler(sp =>
                CreateHandler(sp.GetRequiredService<IOptions<OutboundHttpOptions>>().Value))
            .AddHttpMessageHandler<SafeRedirectHandler>();
    }

    /// <summary>A <see cref="SocketsHttpHandler"/> that refuses any target the policy blocks.</summary>
    public static SocketsHttpHandler CreateHandler(OutboundHttpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var ports = options.AllowedPorts.ToHashSet();

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = options.ConnectTimeout,
            // A proxy from the environment would make the proxy, not the target, the address we
            // check — and then connect to wherever it forwards.
            UseProxy = false,
            ConnectCallback = async (context, ct) =>
            {
                if (context.InitialRequestMessage.RequestUri?.Scheme != Uri.UriSchemeHttps
                    || !ports.Contains(context.DnsEndPoint.Port))
                    throw new DomainException(RefusedCode);

                var address = await ResolveAsync(context.DnsEndPoint.Host, ct)
                    ?? throw new DomainException(RefusedCode);

                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }

    /// <summary>
    /// Checks a URL before it is saved, so an administrator learns it at once instead of on the
    /// first call. Returns the reason in French, or null when it may be stored. Resolution is NOT
    /// done here: a name may resolve differently tomorrow, which is why the handler checks again.
    /// </summary>
    public static string? ValidateUrl(string? url, OutboundHttpOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "L’adresse est obligatoire.";

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return "L’adresse n’est pas une URL valide.";

        if (uri.Scheme != Uri.UriSchemeHttps)
            return "Seules les adresses HTTPS sont acceptées.";

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "L’adresse ne peut pas contenir d’identifiants.";

        var ports = (options ?? new OutboundHttpOptions()).AllowedPorts;
        if (!ports.Contains(uri.Port))
            return $"Port non autorisé ({uri.Port}). Ports acceptés : {string.Join(", ", ports)}.";

        // DnsSafeHost: an IPv6 literal without its brackets, an IDN in its ASCII form.
        var host = uri.DnsSafeHost;
        if (IPAddress.TryParse(host, out var literal) && EgressAddressPolicy.IsBlocked(literal))
            return "Cette adresse est réservée au réseau interne.";

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return "Cette adresse est réservée au réseau interne.";

        return null;
    }

    /// <summary>
    /// The first address the name resolves to that the policy allows, or null. A name whose every
    /// answer is internal is refused rather than reduced to its least-bad answer.
    /// </summary>
    private static async Task<IPAddress?> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
            return EgressAddressPolicy.IsBlocked(literal) ? null : literal;

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException)
        {
            return null;
        }

        return addresses.FirstOrDefault(a => !EgressAddressPolicy.IsBlocked(a));
    }
}

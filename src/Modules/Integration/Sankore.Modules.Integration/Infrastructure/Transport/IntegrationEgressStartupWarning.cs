namespace Sankore.Modules.Integration.Infrastructure.Transport;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Says out loud, once, at start-up, that the egress guard has been deliberately loosened.
///
/// <para>
/// A hosted service rather than a line inside the registration method because the registration
/// has no service provider and therefore no logger — and because the point is a message in the
/// boot log, where an operator reading why a deposit reached a private address will look. The
/// repo's precedent is <c>CorsSetup</c>, which warns at boot when the policy allows no origin:
/// a relaxed guard that announces nothing is a guard nobody remembers two deployments later.
/// </para>
///
/// <para>
/// It logs NOTHING in the default configuration. A boot line per protection that is correctly
/// on is how a boot log becomes unreadable.
/// </para>
/// </summary>
internal sealed class IntegrationEgressStartupWarning(
    IOptions<IntegrationEgressOptions> options,
    ILogger<IntegrationEgressStartupWarning> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.AllowPrivateAddresses)
            logger.LogWarning(
                "{Setting}:AllowPrivateAddresses is TRUE: direct SFTP deposits may reach "
                + "loopback, private, link-local and CGNAT addresses. This is correct only for a "
                + "single-tenant, self-hosted installation whose core banking system shares this "
                + "network. On a multi-tenant host it lets a tenant-editable SftpHost point at "
                + "SANKORE's own services, including the cloud metadata endpoint at "
                + "169.254.169.254 — set it back to false and route on-premise systems through a "
                + "relay agent (IntegrationMode.Relay).",
                IntegrationEgressOptions.SectionName);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

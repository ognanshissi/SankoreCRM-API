namespace Sankore.Modules.Integration.Infrastructure.Transport;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// The registration half of the seam: picks the carrier for one connection so no caller ever
/// branches on it (INT-24, criterion 4).
///
/// <para>
/// The routing key is <c>RelayAgentId</c> and not <see cref="IntegrationMode"/>, which needs
/// saying because the interface's own documentation points at the mode. A file-exchanging
/// connection's mode is <see cref="IntegrationMode.Batch"/> — that is what routes its commands to
/// the batch socle instead of to an adapter in the first place — so the mode cannot also express
/// WHERE the deposit happens. <c>RelayAgentId</c> can, it is server-set only (INT-27's enrolment
/// is the single writer), and it says exactly the thing that matters here: whether this
/// installation has a route into the IMF's network of its own, or whether an agent holds the only
/// one.
/// </para>
///
/// <para>
/// <see cref="IntegrationMode.Relay"/> is honoured too, for the connection whose mode says relay
/// and whose agent link has not been established yet: it must not silently fall through to a
/// direct connection this process cannot legitimately make. <see cref="RelayFileTransport"/>
/// answers that case with a Technical failure naming the missing enrolment.
/// </para>
/// </summary>
internal sealed class IntegrationFileTransportRouter(
    SftpFileTransport direct,
    RelayFileTransport relay) : IIntegrationFileTransport
{
    public Task<IntegrationResult> PutAsync(
        IntegrationConnection connection, string fileName, byte[] content, CancellationToken ct)
        => Route(connection).PutAsync(connection, fileName, content, ct);

    public Task<IntegrationResult<IReadOnlyList<string>>> ListInboundAsync(
        IntegrationConnection connection, CancellationToken ct)
        => Route(connection).ListInboundAsync(connection, ct);

    public Task<IntegrationResult<byte[]>> GetInboundAsync(
        IntegrationConnection connection, string fileName, long maxBytes, CancellationToken ct)
        => Route(connection).GetInboundAsync(connection, fileName, maxBytes, ct);

    public Task<IntegrationResult> ArchiveInboundAsync(
        IntegrationConnection connection, string fileName, CancellationToken ct)
        => Route(connection).ArchiveInboundAsync(connection, fileName, ct);

    private IIntegrationFileTransport Route(IntegrationConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.RelayAgentId is not null || connection.Mode == IntegrationMode.Relay
            ? relay
            : direct;
    }
}

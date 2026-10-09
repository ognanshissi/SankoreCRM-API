namespace Sankore.Modules.Integration.Features.Connections.SetConnectionSecret;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Stores one of a connection's credentials in M12's vault (INT-03, criterion 3).
///
/// <para>
/// The connection row keeps only the REFERENCE. There is no column, no event and no projection
/// holding the value: one home is what keeps it out of a GET, a log and an audit payload.
/// </para>
///
/// <para>
/// Keyed per connection and not per tenant: an IMF has a CBS and two insurers at once, each with
/// its own credentials, and a tenant-wide key would make saving the second one destroy the first.
/// </para>
/// </summary>
/// <param name="Name">
/// Which credential — one of <c>connection-credential</c>, <c>sftp-credential</c>,
/// <c>webhook-secret</c>. The vault's own slot names, so a rotation script and a screen cannot
/// disagree about what they are writing.
/// </param>
/// <param name="Value">
/// <see cref="SensitiveDataAttribute"/> is load-bearing, not decoration: <c>ICommand</c> makes
/// AuditBehavior serialize this record into <c>audit.entries</c>, and without it the credential
/// would be written in clear to the one table built to be read later. The attribute replaces it
/// with "***" there — the same thing M12 does for its provider credential, and M02 for its
/// biometry token. The vault is the only place the value lands.
/// </param>
/// <param name="ExpiresAt">
/// When the far end says the credential dies. Carried to the vault so a screen can warn before a
/// batch cycle starts failing at 2 a.m. over an expired key.
/// </param>
internal sealed record SetConnectionSecretCommand(
    Guid ConnectionId,
    string Name,
    [property: SensitiveData] string Value,
    DateTimeOffset? ExpiresAt = null)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "IntegrationConnection";

    public string? ResourceId => ConnectionId.ToString();
}

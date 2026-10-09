namespace Sankore.Modules.Integration.Features.Connections.CreateConnection;

using MediatR;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Declares one tenant's link to one external system (INT-03).
///
/// <para>
/// The connection is created INACTIVE and no endpoint can change that in the same call: INT-03
/// requires a passed health check before activation, so creating and activating are two steps on
/// purpose. A single "create active" would make the health check optional in practice.
/// </para>
///
/// <para>
/// There is no credential in this command. The vault is written through
/// <c>SetConnectionSecretCommand</c>, which carries <c>[SensitiveData]</c>; folding the value in
/// here would put it in the audit payload of every connection creation.
/// </para>
///
/// <para>
/// There is no <c>RelayAgentId</c> either, and its absence is deliberate — the same rule M13
/// applies to <c>Lead.LeadSourceConfigId</c>, with a wider blast radius. Nothing here can
/// validate such an id (relay-agent registration is INT-27), and a client able to set it could
/// name ANOTHER TENANT's on-premise agent: that tenant's network would then execute this
/// tenant's command payloads — identity documents, addresses, declared income — and this tenant
/// could read the other's SFTP directories and SQL views. It leaks in both directions, so the
/// field is server-set only and INT-27's enrolment flow owns it.
/// </para>
/// </summary>
internal sealed record CreateConnectionCommand(
    IntegrationFamily Family,
    IntegrationKind Kind,
    IntegrationMode Mode,
    string Name,
    ConnectionSettings? Settings)
    : IRequest<Result<Guid>>, ICommand, IResourceCommand, IConnectionSettingsCarrier
{
    public string ResourceType => "IntegrationConnection";

    /// <summary>Null: the id does not exist until the handler has run.</summary>
    public string? ResourceId => null;
}

namespace Sankore.Modules.Integration.Features.Connections.UpdateConnection;

using MediatR;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Replaces the coordinates of one connection. PUT semantics: the settings object is written
/// whole, because a partial merge of a polymorphic record cannot say the difference between
/// "leave tokenEndpoint alone" and "clear it".
///
/// <para>
/// <see cref="Family"/> and <see cref="Kind"/> are deliberately absent. Changing the kind of a
/// live connection would leave its references, commands and call logs pointing at a system that
/// never knew them; a new system is a new connection.
/// </para>
///
/// <para>
/// So is <c>RelayAgentId</c>, and for a sharper reason — the same rule M13 applies to
/// <c>Lead.LeadSourceConfigId</c>, here with a cross-tenant blast radius. Nothing in this module
/// can validate such an id (relay-agent registration is INT-27, not built), and a client able to
/// set it could point this connection at ANOTHER TENANT's on-premise agent: that tenant's network
/// would execute this tenant's command payloads — identity documents, addresses, declared
/// income — while this tenant read the other's SFTP directories and SQL views. The handler
/// carries the stored value through untouched, so an existing link is preserved rather than
/// silently cleared, and INT-27's enrolment flow remains the only writer.
/// </para>
/// </summary>
/// <param name="ExpectedVersion">
/// The PostgreSQL <c>xmin</c> token the caller read. The repo's optimistic-concurrency idiom.
/// </param>
/// <param name="ExpectedUpdatedAt">
/// The alternative token, for a client that holds the DTO's <c>updatedAt</c> but not the row
/// version. Compared exactly — both values come from the same column, so there is no rounding to
/// tolerate. At least one of the two is required: without either, two administrators editing the
/// same connection would each silently overwrite the other.
/// </param>
internal sealed record UpdateConnectionCommand(
    Guid ConnectionId,
    string Name,
    IntegrationMode Mode,
    ConnectionSettings? Settings,
    uint? ExpectedVersion = null,
    DateTimeOffset? ExpectedUpdatedAt = null)
    : IRequest<Result>, ICommand, IResourceCommand, IConnectionSettingsCarrier
{
    public string ResourceType => "IntegrationConnection";

    public string? ResourceId => ConnectionId.ToString();
}

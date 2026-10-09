namespace Sankore.Modules.Integration.Features.RelayAgents.RevokeRelayAgent;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Revokes a relay agent: the certificate stops being admitted and the enrolment token, if one is
/// still armed, is burned (INT-27, criterion 2).
///
/// <para>
/// Revoked, never deleted. Connections, call logs and batch files carry this id, and a deleted row
/// would make a year of audit trail unreadable — the same rule the connections area follows, which
/// is why there is no DELETE anywhere in this module.
/// </para>
/// </summary>
internal sealed record RevokeRelayAgentCommand(Guid AgentId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "IntegrationRelayAgent";

    public string? ResourceId => AgentId.ToString();
}

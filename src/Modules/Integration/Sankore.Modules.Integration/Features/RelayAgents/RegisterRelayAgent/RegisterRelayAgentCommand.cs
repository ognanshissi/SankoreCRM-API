namespace Sankore.Modules.Integration.Features.RelayAgents.RegisterRelayAgent;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Registers one on-premise relay agent for the current tenant and arms its single-use enrolment
/// token (INT-27, criterion 1).
///
/// <para>
/// The command carries a name and nothing else. It does <b>not</b> carry an agent id, a tenant id
/// or a connection id: the agent's identity is minted here and its tenant is read from the JWT,
/// which is the whole of the rule in docs/integration-module-plan.md §5bis (a) — the link between
/// a connection and an agent is established by this flow, never by accepting an identifier in a
/// request body. The relay executes orders inside the institution's own network (local HTTP, SFTP,
/// read-only SQL), so an id a tenant could choose would let one tenant route its writes through
/// another tenant's network and read its directories.
/// </para>
///
/// <para>
/// <see cref="ICommand"/> and <see cref="IResourceCommand"/> are criterion 4: they are what put
/// the registration in <c>audit.entries</c> with its actor, its outcome and the agent it concerns.
/// The command is what gets audited, and the command holds no credential — the token is minted
/// inside the handler and travels back in the RESPONSE, which the audit pipeline does not
/// serialise.
/// </para>
/// </summary>
/// <param name="Name">
/// Operator-facing, so two sites of one institution can be told apart in the list and in a log
/// line. Not an identifier: nothing resolves an agent by name.
/// </param>
internal sealed record RegisterRelayAgentCommand(string Name)
    : IRequest<Result<RelayAgentEnrolmentDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "IntegrationRelayAgent";

    /// <summary>
    /// Null: the id is minted by the handler, so it cannot be named by the command that asks for
    /// it. The handler logs the id it created, and the audit row's action plus actor are what a
    /// later question ("who registered an agent that afternoon") is answered from.
    /// </summary>
    public string? ResourceId => null;
}

/// <summary>
/// The answer to a registration — <b>the only time the clear enrolment token is ever readable</b>.
///
/// <para>
/// Only its SHA-256 hash is stored, so this module is unable to show it again: there is no GET
/// that returns it and no way to recover it from the row. That is deliberate and it is the whole
/// security property of criterion 1 — a token retrievable from a screen is a permanent credential
/// with an audit row attached, not a single-use one. An operator who loses it registers another
/// agent and revokes this one.
/// </para>
/// </summary>
/// <param name="EnrolmentToken">
/// The clear token, to be handed to whoever installs the agent. Returned once, never logged,
/// never audited, never stored.
/// </param>
/// <param name="ExpiresAt">
/// When presenting it stops working. Short on purpose — this is an installation window, not a
/// mail-reading window.
/// </param>
internal sealed record RelayAgentEnrolmentDto(
    Guid AgentId,
    string Name,
    string EnrolmentToken,
    DateTimeOffset ExpiresAt);

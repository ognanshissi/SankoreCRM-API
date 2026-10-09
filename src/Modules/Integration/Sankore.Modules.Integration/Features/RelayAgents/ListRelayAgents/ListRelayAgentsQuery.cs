namespace Sankore.Modules.Integration.Features.RelayAgents.ListRelayAgents;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// The tenant's relay agents, with their last contact, reported version and state (INT-27,
/// criterion 3 — the read half).
///
/// <para>
/// A query: it does NOT implement <c>ICommand</c>, so neither <c>TransactionBehavior</c> nor
/// <c>AuditBehavior</c> wraps it. Reading whether a relay is up is not an event worth an audit
/// row — and criterion 4's "all actions are audited" is about the actions, the registration, the
/// exchange, the revocation and the heartbeat, each of which carries the marker.
/// </para>
///
/// <para>
/// Unpaged, and that is a decision rather than an omission: an institution runs one relay per
/// site, so a handful of rows at most. A page parameter here would be ceremony on a list that
/// never needs one, and the day a tenant has hundreds of agents something else has gone wrong.
/// </para>
/// </summary>
internal sealed record ListRelayAgentsQuery : IRequest<Result<IReadOnlyList<RelayAgentDto>>>;

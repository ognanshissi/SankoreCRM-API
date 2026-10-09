namespace Sankore.Integration.RelayAgent.Execution;

using Sankore.Integration.RelayAgent.Protocol;

/// <summary>
/// One of the three things an order can ask for (criterion 3), plus the read half of SFTP.
///
/// <para>
/// <b>No executor is given an <c>ILogger</c>, and that is the structural half of criterion 4.</b>
/// The guarantee "logs contain no personal data" is otherwise a rule somebody has to keep
/// remembering while they debug an SFTP problem at two in the morning. Here they cannot break it
/// without first changing a constructor signature, which is the kind of change a review notices.
/// Everything about an order is logged by <c>RelayOrderDispatcher</c>, through
/// <c>Observability/RelayLog.cs</c>, from values that cannot carry relayed content.
/// </para>
/// </summary>
public interface IRelayOrderExecutor
{
    /// <summary>Which kind of order this executor claims.</summary>
    RelayOrderKind Kind { get; }

    /// <summary>
    /// Runs the order. Must not throw for any condition of the relayed system: an unreachable
    /// host, a refused credential, a timeout and a missing file are all
    /// <see cref="RelayExecution"/> values.
    /// </summary>
    Task<RelayExecution> ExecuteAsync(RelayOrder order, CancellationToken cancellationToken);
}

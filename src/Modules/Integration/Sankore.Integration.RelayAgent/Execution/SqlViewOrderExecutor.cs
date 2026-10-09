namespace Sankore.Integration.RelayAgent.Execution;

using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Sankore.Integration.RelayAgent.Configuration;
using Sankore.Integration.RelayAgent.Observability;
using Sankore.Integration.RelayAgent.Protocol;

/// <summary>
/// Criterion 3, the read-only SQL query on a DECLARED view — the narrowest target of the three
/// and the one whose constraint matters most.
///
/// <para>
/// <b>No part of the statement comes from the wire, and there is no code path by which it could.</b>
/// The order carries a target name and a dictionary of filter values. The statement is assembled
/// here from the declared schema, the declared view and the declared filter columns, each of
/// which <c>RelayAgentOptionsValidator</c> has already proven to be a bare SQL identifier before
/// the process finished starting. The values are bound as parameters and never concatenated.
/// </para>
///
/// <para>
/// This is not defensive style; it is the component's reason to be shaped this way. A relay that
/// executed SQL it was sent would be a remote-code-execution channel into a bank's database,
/// reachable by anything that obtained the platform's end of the session — and the platform is a
/// multi-tenant web application on the public internet. The agent is the last place that can
/// refuse, so it refuses structurally: <see cref="RelaySqlViewBody"/> has no field for a query,
/// a table, a column list or an ordering.
/// </para>
///
/// <para>
/// Three further locks, in order of how much they are worth:
/// </para>
/// <list type="number">
///   <item>
///     <b>The database role.</b> The connection string in the file should name a role with
///     <c>SELECT</c> on this view and nothing else. Only the IMF can set this, and it is the only
///     lock that survives a fault in this file.
///   </item>
///   <item>
///     <b>A read-only transaction.</b> <c>SET TRANSACTION READ ONLY</c> makes the session itself
///     incapable of a write, so even a view with an <c>INSTEAD OF</c> trigger cannot be written
///     through.
///   </item>
///   <item>
///     <b>A <c>LIMIT</c> from the file.</b> The rows are held in memory to be answered, and a
///     filter the platform forgot to send would otherwise return a bank's whole customer table
///     over the channel.
///   </item>
/// </list>
///
/// <para>
/// PostgreSQL only: Npgsql is the one driver this agent ships. An IMF whose reporting database is
/// Oracle or SQL Server needs a driver added here — stated rather than pretended, because a
/// "database-agnostic" relay that silently supports one engine is worse than one that says so.
/// </para>
/// </summary>
public sealed class SqlViewOrderExecutor : IRelayOrderExecutor
{
    private readonly Dictionary<string, RelaySqlViewTargetOptions> _targets;

    public SqlViewOrderExecutor(IOptions<RelayAgentOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _targets = options.Value.SqlViewTargets.ToDictionary(
            t => t.Name, StringComparer.OrdinalIgnoreCase);
    }

    public RelayOrderKind Kind => RelayOrderKind.SqlView;

    public async Task<RelayExecution> ExecuteAsync(
        RelayOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (!_targets.TryGetValue(order.Target, out var target))
            return RelayExecution.Refuse(RelayErrorCodes.TargetNotDeclared);

        if (!OrderBody.TryRead<RelaySqlViewBody>(order, out var body) || body is null)
            return RelayExecution.Refuse(RelayErrorCodes.FrameInvalid);

        var filters = body.Parameters ?? new Dictionary<string, string?>();

        // Every supplied column must be declared. Refused and not ignored: ignoring an unknown
        // filter would silently widen the result set — the platform believes it asked for one
        // customer and receives the cap's worth of them.
        foreach (var column in filters.Keys)
        {
            if (!target.Parameters.Any(p => p.Equals(column, StringComparison.OrdinalIgnoreCase)))
                return RelayExecution.Refuse(RelayErrorCodes.ParameterNotDeclared);
        }

        var budget = TimeSpan.FromSeconds(
            order.TimeoutSeconds is > 0 and var requested && requested < target.TimeoutSeconds
                ? requested
                : target.TimeoutSeconds);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget);

        try
        {
            await using var connection = new NpgsqlConnection(target.ConnectionString);
            await connection.OpenAsync(timeout.Token).ConfigureAwait(false);

            // IsolationLevel.Snapshot is not what makes this read-only — the SET below is. The
            // transaction exists so there is a scope for that statement to apply to.
            await using var transaction = await connection
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token)
                .ConfigureAwait(false);

            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            {
                await readOnly.ExecuteNonQueryAsync(timeout.Token).ConfigureAwait(false);
            }

            await using var command = new NpgsqlCommand(
                BuildStatement(target, filters, out var bindings), connection, transaction);

            command.CommandTimeout = (int)budget.TotalSeconds;

            foreach (var (name, value) in bindings)
            {
                // Text in, text compared. The view is expected to expose its filter columns as
                // text or to accept an implicit cast; typing them here would mean this agent
                // deciding how a date or a decimal is parsed, in whatever locale a branch server
                // runs — the bug the repository's spreadsheet importers were written to prevent.
                command.Parameters.Add(new NpgsqlParameter<string?>(name, value));
            }

            await using var reader = await command
                .ExecuteReaderAsync(timeout.Token).ConfigureAwait(false);

            var result = await ReadRowsAsync(reader, target.MaxRows, timeout.Token)
                .ConfigureAwait(false);

            // Read-only, so committing and rolling back are the same thing; rolled back because
            // it says what happened.
            await transaction.RollbackAsync(timeout.Token).ConfigureAwait(false);

            return RelayExecution.Ok(
                JsonSerializer.SerializeToElement(result, RelayProtocolJson.Options));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RelayExecution.Unavailable(
                RelayErrorCodes.TargetTimeout, nameof(OperationCanceledException));
        }
        catch (NpgsqlException ex) when (ex is PostgresException)
        {
            // The server answered and refused: a dropped view, a revoked grant, a column that is
            // no longer there. Reached — so the operator looks at the database, not the network.
            // The exception's message is NOT used: PostgresException's detail quotes the offending
            // row, which is the one thing that must not reach a log.
            return RelayExecution.Unavailable(
                RelayErrorCodes.TargetError, ex.GetType().Name, reachedTarget: true);
        }
        catch (NpgsqlException ex)
        {
            return RelayExecution.Unavailable(RelayErrorCodes.TargetUnreachable, ex.GetType().Name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // A malformed connection string reaches here, and only on first use.
            return RelayExecution.Unexpected(ex);
        }
    }

    /// <summary>
    /// Assembles the statement. Every interpolated fragment is a validated identifier; every
    /// value is a bound parameter.
    ///
    /// <para>
    /// Internal rather than private so the test project can assert the shape directly — the one
    /// place in this project where the generated text is worth pinning, because a change to it is
    /// the only way the injection property could be lost.
    /// </para>
    /// </summary>
    internal static string BuildStatement(
        RelaySqlViewTargetOptions target,
        IReadOnlyDictionary<string, string?> filters,
        out List<KeyValuePair<string, string?>> bindings)
    {
        bindings = [];

        var sql = new StringBuilder("SELECT * FROM ")
            .Append('"').Append(target.Schema).Append('"')
            .Append('.')
            .Append('"').Append(target.View).Append('"');

        // Iterated over the DECLARED columns and not over the supplied filters, so the clause
        // order is the file's and not the wire's — a dictionary's enumeration order is not a
        // contract, and a statement whose text varies run to run defeats PostgreSQL's plan cache.
        var first = true;
        var index = 0;

        foreach (var column in target.Parameters)
        {
            var supplied = filters.FirstOrDefault(
                f => f.Key.Equals(column, StringComparison.OrdinalIgnoreCase));

            if (supplied.Key is null) continue;

            sql.Append(first ? " WHERE " : " AND ")
               .Append('"').Append(column).Append('"')
               .Append(" = @p").Append(index.ToString(CultureInfo.InvariantCulture));

            bindings.Add(new KeyValuePair<string, string?>(
                "p" + index.ToString(CultureInfo.InvariantCulture), supplied.Value));

            first = false;
            index++;
        }

        // One row over the cap, so "exactly the cap" can be told from "there were more".
        sql.Append(" LIMIT ")
           .Append((target.MaxRows + 1).ToString(CultureInfo.InvariantCulture));

        return sql.ToString();
    }

    private static async Task<RelaySqlViewResult> ReadRowsAsync(
        NpgsqlDataReader reader, int maxRows, CancellationToken cancellationToken)
    {
        var columns = new List<string>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
            columns.Add(reader.GetName(i));

        var rows = new List<IReadOnlyList<string?>>();
        var truncated = false;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count == maxRows)
            {
                truncated = true;
                break;
            }

            var row = new string?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                // InvariantCulture, explicitly: a numeric column rendered on a fr-FR host would
                // arrive with a comma for a decimal point and be re-parsed as something else
                // entirely on the platform side.
                row[i] = reader.IsDBNull(i)
                    ? null
                    : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
            }

            rows.Add(row);
        }

        return new RelaySqlViewResult(columns, rows, truncated);
    }
}

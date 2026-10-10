namespace Sankore.Integration.RelayAgent.Tests;

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Sankore.Integration.RelayAgent.Execution;
using Sankore.Integration.RelayAgent.Observability;
using Sankore.Integration.RelayAgent.Protocol;
using Xunit;

/// <summary>
/// Criterion 4, made checkable: "its logs contain no personal data".
///
/// <para>
/// The structural half of that guarantee is that no executor is handed an <c>ILogger</c>, so only
/// <see cref="RelayOrderDispatcher"/> can log — and only through <see cref="RelayLog"/>, whose
/// signatures accept nothing that can carry relayed content. These tests are the behavioural
/// half: a real dispatch with a payload full of the kind of data a bank's systems return, and an
/// assertion over every captured log line, formatted message and structured property.
/// </para>
///
/// <para>
/// The failure path is tested separately and matters more. An exception's message is where
/// personal data actually leaks — SSH.NET names the remote path, Npgsql names the column and
/// sometimes the value — so the test throws an exception whose message contains a customer name,
/// an account number and a file path, and asserts that none of the three reaches a log line.
/// </para>
/// </summary>
public sealed class LogRedactionTests
{
    /// <summary>
    /// Strings that must never appear in any log line, whatever happens. A surname, a phone
    /// number, an account reference, a national identity number and a remote path — the five
    /// shapes a relayed answer actually carries.
    /// </summary>
    private static readonly string[] MustNeverAppear =
    [
        "KOUASSI",
        "+2250700000000",
        "CI0012345678",
        "C 1987 1234",
        "/echange/sankore/entrant",
    ];

    [Fact]
    public async Task A_successful_order_logs_no_part_of_its_payload()
    {
        var logger = new CapturingLogger();

        var payload = JsonSerializer.SerializeToElement(new
        {
            customer = "KOUASSI Awa",
            phone = "+2250700000000",
            account = "CI0012345678",
            idDocument = "C 1987 1234",
        });

        var dispatcher = Build(logger, RelayExecution.Ok(payload));

        var result = await dispatcher.DispatchAsync(Order(RelayOrderKind.SqlView), default);

        result.Outcome.Should().Be(RelayOutcome.Succeeded);

        // The payload itself still travels back on the wire — that is the point of a relay. What
        // must not happen is it reaching a log.
        result.Payload.Should().NotBeNull();

        AssertClean(logger);
    }

    [Fact]
    public async Task A_failing_order_logs_the_exception_type_and_never_its_message()
    {
        var logger = new CapturingLogger();

        // The shape of a real SSH.NET or Npgsql message: the operation, the path, and the data.
        var leaky = new InvalidOperationException(
            "Permission denied writing /echange/sankore/entrant/KOUASSI-CI0012345678.csv "
            + "for +2250700000000 (C 1987 1234)");

        var dispatcher = Build(logger, throws: leaky);

        var result = await dispatcher.DispatchAsync(Order(RelayOrderKind.SftpPut), default);

        result.Outcome.Should().Be(RelayOutcome.Unavailable);
        result.ErrorCode.Should().Be(RelayErrorCodes.RelayUnexpectedError);

        // The type name IS logged, and is the whole of what a reader gets about the cause.
        logger.Lines.Should().Contain(line => line.Contains(nameof(InvalidOperationException),
            StringComparison.Ordinal));

        AssertClean(logger);
    }

    [Fact]
    public async Task A_refused_order_logs_only_its_code()
    {
        var logger = new CapturingLogger();
        var dispatcher = Build(logger, RelayExecution.Refuse(RelayErrorCodes.TargetNotDeclared));

        var result = await dispatcher.DispatchAsync(Order(RelayOrderKind.HttpCall), default);

        result.Outcome.Should().Be(RelayOutcome.Refused);
        result.ErrorCode.Should().Be(RelayErrorCodes.TargetNotDeclared);
        result.Payload.Should().BeNull();

        AssertClean(logger);
    }

    [Fact]
    public async Task The_result_frame_of_a_failure_carries_no_payload()
    {
        // Pinned because the result frame is the other way data leaves this process. A failure
        // reports a code and a duration; a partial answer to a failed order would be a payload
        // nobody on the platform side expected to have to handle.
        var dispatcher = Build(
            new CapturingLogger(),
            RelayExecution.Unavailable(RelayErrorCodes.TargetTimeout, "SocketException"));

        var result = await dispatcher.DispatchAsync(Order(RelayOrderKind.SqlView), default);

        result.Payload.Should().BeNull();
        result.ErrorCode.Should().Be(RelayErrorCodes.TargetTimeout);
    }

    private static void AssertClean(CapturingLogger logger)
    {
        logger.Lines.Should().NotBeEmpty("the dispatcher must log every order it handles");

        foreach (var secret in MustNeverAppear)
        {
            logger.Lines.Should().NotContain(
                line => line.Contains(secret, StringComparison.OrdinalIgnoreCase),
                $"'{secret}' is relayed data and must never reach a log line");
        }
    }

    private static RelayOrder Order(RelayOrderKind kind) => new(
        CorrelationId: "11111111-2222-3333-4444-555555555555",
        Kind: kind,
        Target: "cbs-api",
        Body: JsonSerializer.SerializeToElement(new { }),
        TimeoutSeconds: null);

    /// <summary>
    /// A dispatcher wired to a stub for every order kind, so a test can dispatch whichever kind
    /// reads best without the dispatcher refusing it for want of an executor.
    /// </summary>
    private static RelayOrderDispatcher Build(
        CapturingLogger logger, RelayExecution? returns = null, Exception? throws = null)
        => new(
            Enum.GetValues<RelayOrderKind>()
                .Select(kind => (IRelayOrderExecutor)new StubExecutor(kind, returns, throws))
                .ToList(),
            new TargetHealthRegistry(TimeProvider.System),
            logger);

    /// <summary>Stands in for a real executor: one behaviour, one claimed kind.</summary>
    private sealed class StubExecutor(
        RelayOrderKind kind, RelayExecution? returns, Exception? throws) : IRelayOrderExecutor
    {
        public RelayOrderKind Kind => kind;

        public Task<RelayExecution> ExecuteAsync(
            RelayOrder order, CancellationToken cancellationToken)
            => throws is not null
                ? throw throws
                : Task.FromResult(returns ?? RelayExecution.Refuse(RelayErrorCodes.FrameInvalid));
    }

    /// <summary>
    /// Captures both the formatted message and every structured property value, because a
    /// structured logging sink writes the properties and not only the rendered line — a leak
    /// through a property would be invisible to a test that read messages alone.
    /// </summary>
    private sealed class CapturingLogger : ILogger<RelayOrderDispatcher>
    {
        public List<string> Lines { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => new NoScope();

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));

            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                foreach (var property in properties)
                    Lines.Add($"{property.Key}={property.Value}");
            }

            // An exception passed to the logger is a leak even when the message is clean, because
            // a sink serialises it. Captured so the assertions see it.
            if (exception is not null) Lines.Add(exception.ToString());
        }

        private sealed class NoScope : IDisposable
        {
            public void Dispose() { }
        }
    }
}

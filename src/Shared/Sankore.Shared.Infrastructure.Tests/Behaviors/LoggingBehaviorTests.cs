namespace Sankore.Shared.Infrastructure.Tests.Behaviors;

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Features.Fake;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class LoggingBehaviorTests
{
    // ── Fake logger that captures every Log() call ─────────────────────

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

        public List<LogEntry> Entries { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new(logLevel, formatter(state, exception), exception));
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private record TestCommand : MediatR.IRequest<Result>;

    private static LoggingBehavior<TestCommand, Result> BuildBehavior(
        CapturingLogger<LoggingBehavior<TestCommand, Result>> logger) => new(logger);

    // ── S1: Success → LogInformation with SUCCESS outcome ─────────────

    [Fact]
    public async Task Logs_information_on_success()
    {
        var logger = new CapturingLogger<LoggingBehavior<TestCommand, Result>>();

        await BuildBehavior(logger).Handle(
            new TestCommand(),
            () => Task.FromResult(Result.Ok()),
            CancellationToken.None);

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information && e.Message.Contains("SUCCESS"));
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }

    // ── S2: Domain failure → LogWarning with FAILURE outcome ──────────

    [Fact]
    public async Task Logs_warning_on_domain_failure()
    {
        var logger = new CapturingLogger<LoggingBehavior<TestCommand, Result>>();

        await BuildBehavior(logger).Handle(
            new TestCommand(),
            () => Task.FromResult(Result.Fail("validation failed")),
            CancellationToken.None);

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("FAILURE")
            && e.Message.Contains("validation failed"));
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }

    // ── S3: Exception → LogError and rethrown ─────────────────────────

    [Fact]
    public async Task Logs_error_and_rethrows_on_exception()
    {
        var logger = new CapturingLogger<LoggingBehavior<TestCommand, Result>>();
        var boom = new InvalidOperationException("db unavailable");

        var act = () => BuildBehavior(logger).Handle(
            new TestCommand(),
            () => throw boom,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("db unavailable");

        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Error && e.Exception == boom);
    }

    // ── S4: Module extracted from request namespace ────────────────────

    [Fact]
    public async Task Extracts_module_name_from_request_namespace()
    {
        // FakeLeadsRequest is declared in "Sankore.Modules.Leads.Features.Fake"
        // → ExtractModule must resolve "Leads" and include it in the log message.
        var logger = new CapturingLogger<LoggingBehavior<FakeLeadsRequest, Result>>();

        await new LoggingBehavior<FakeLeadsRequest, Result>(logger).Handle(
            new FakeLeadsRequest(),
            () => Task.FromResult(Result.Ok()),
            CancellationToken.None);

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information && e.Message.Contains("Leads"));
    }
}

namespace Sankore.Modules.Integration.Tests.Infrastructure.CallLog;

using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure.CallLog;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// INT-08 criterion 1: <b>every</b> adapter call writes a row, carrying operation, endpoint,
/// status, duration, error family and correlation id.
///
/// <para>
/// "Every" is the part worth testing. A journal that records the happy path is not a weaker
/// journal, it is a misleading one — the missing rows read as idle periods, so the gaps caused by
/// a back-office refusing or timing out look exactly like the gaps caused by nobody calling. So
/// each path gets its own test: success, each failure family, and the operation throwing.
/// </para>
/// </summary>
public sealed class CallJournalTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Connection = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly RecordingStore _store = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-03-04T10:00:00Z", CultureInfo.InvariantCulture));

    private CallJournal Journal() => new(_store, _clock, NullLogger<CallJournal>.Instance);

    [Fact]
    public async Task Records_a_row_on_success_and_returns_the_result_untouched()
    {
        var context = new CallContext(Tenant, Connection, "CreateCustomer", Endpoint: "/party/v2/customers");

        var result = await Journal().RecordAsync(
            context, _ => Task.FromResult(IntegrationResult.Ok(new ExternalId("CBS-1"))), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be("CBS-1");

        var row = _store.Rows.Should().ContainSingle().Which;
        row.TenantId.Should().Be(Tenant);
        row.ConnectionId.Should().Be(Connection);
        row.Operation.Should().Be("CreateCustomer");
        row.Endpoint.Should().Be("/party/v2/customers");
        row.ErrorFamily.Should().BeNull();
        row.ErrorCode.Should().BeNull();
        row.At.Should().Be(_clock.GetUtcNow());
        row.CorrelationId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Records_the_void_result_overload_too()
    {
        var context = new CallContext(Tenant, Connection, "SetKycLevel");

        var result = await Journal().RecordAsync(
            context, _ => Task.FromResult(IntegrationResult.Ok()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _store.Rows.Should().ContainSingle().Which.Operation.Should().Be("SetKycLevel");
    }

    [Theory]
    [InlineData(nameof(ErrorFamily.Transient), ErrorFamily.Transient)]
    [InlineData(nameof(ErrorFamily.Functional), ErrorFamily.Functional)]
    [InlineData(nameof(ErrorFamily.Technical), ErrorFamily.Technical)]
    public async Task Records_the_error_family_of_a_failed_call(string label, ErrorFamily expected)
    {
        var failure = expected switch
        {
            ErrorFamily.Transient => IntegrationResult.Transient(IntegrationErrors.Timeout),
            ErrorFamily.Functional => IntegrationResult.Functional(IntegrationErrors.Duplicate),
            _ => IntegrationResult.Technical(IntegrationErrors.MappingMissing),
        };

        var result = await Journal().RecordAsync(
            new CallContext(Tenant, Connection, $"Open{label}"),
            _ => Task.FromResult(failure),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue("the journal observes, it never rewrites the outcome");

        var row = _store.Rows.Should().ContainSingle().Which;
        row.ErrorFamily.Should().Be(expected);
        row.ErrorCode.Should().Be(failure.Code);
    }

    [Fact]
    public async Task Records_a_technical_row_and_rethrows_when_the_operation_throws()
    {
        var boom = new InvalidOperationException("the adapter's HTTP stack blew up");

        var act = async () => await Journal().RecordAsync(
            new CallContext(Tenant, Connection, "OpenAccount"),
            _ => Task.FromException<IntegrationResult<ExternalId>>(boom),
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);

        var row = _store.Rows.Should().ContainSingle().Which;
        row.ErrorFamily.Should().Be(ErrorFamily.Technical, "an adapter that throws has a bug or a misconfiguration");
        row.ErrorCode.Should().Be("INTEGRATION_UNHANDLED_EXCEPTION");

        // The message is the one place a serialization or database exception quotes the payload.
        row.ErrorCode.Should().NotContain("blew up");
    }

    [Fact]
    public async Task Records_a_transient_row_when_the_caller_cancels()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await Journal().RecordAsync(
            new CallContext(Tenant, Connection, "GetBalance"),
            ct => Task.FromCanceled<IntegrationResult<CbsBalance>>(ct),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        var row = _store.Rows.Should().ContainSingle().Which;
        row.ErrorFamily.Should().Be(
            ErrorFamily.Transient,
            "our own shutdown is not a refusal by the back-office, and must not alert an administrator");
        row.ErrorCode.Should().Be("INTEGRATION_CALL_CANCELLED");
    }

    [Fact]
    public async Task Writes_the_row_even_though_the_callers_token_is_already_cancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Journal().RecordAsync(
            new CallContext(Tenant, Connection, "CheckHealth"),
            _ => Task.FromResult(IntegrationResult.Ok()),
            cts.Token);

        _store.Rows.Should().ContainSingle(
            "the row describes a call that already happened; cancelling its write destroys evidence");
        _store.LastToken.Should().Be(
            CancellationToken.None, "the append must not inherit the token that is tearing the call down");
    }

    [Fact]
    public async Task A_failing_append_never_takes_the_business_call_down()
    {
        var store = Substitute.For<ICallLogStore>();
        store.AppendAsync(Arg.Any<IntegrationCallLog>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new TimeoutException("the journal's own database is unreachable")));

        var journal = new CallJournal(store, _clock, NullLogger<CallJournal>.Instance);

        var result = await journal.RecordAsync(
            new CallContext(Tenant, Connection, "CreateCustomer"),
            _ => Task.FromResult(IntegrationResult.Ok(new ExternalId("CBS-9"))),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(
            "a compliance journal that rolls back the call it describes turns an audit problem "
            + "into an outage");
        result.Value.Value.Should().Be("CBS-9");
    }

    [Fact]
    public async Task A_failing_append_does_not_hide_the_operations_own_exception()
    {
        var store = Substitute.For<ICallLogStore>();
        store.AppendAsync(Arg.Any<IntegrationCallLog>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new TimeoutException("journal down")));

        var journal = new CallJournal(store, _clock, NullLogger<CallJournal>.Instance);
        var boom = new HttpRequestException("connection reset");

        var act = async () => await journal.RecordAsync(
            new CallContext(Tenant, Connection, "OpenAccount"),
            _ => Task.FromException<IntegrationResult>(boom),
            CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Should().BeSameAs(
            boom, "the adapter's failure is the caller's news; the journal's failure is ours");
    }

    [Fact]
    public async Task Times_the_call_rather_than_reporting_zero()
    {
        var result = await Journal().RecordAsync(
            new CallContext(Tenant, Connection, "GetAccounts"),
            async _ =>
            {
                await Task.Delay(25, CancellationToken.None);
                return IntegrationResult.Ok();
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // A loose floor: the point is that a Stopwatch is actually running, not that the scheduler
        // is precise. Asserting an exact figure would make this test fail on a loaded CI agent.
        _store.Rows.Should().ContainSingle().Which.DurationMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task The_correlation_id_is_the_one_the_adapter_put_on_the_wire()
    {
        // The usage this enables: the adapter holds the context it passed in, so the id it sets as
        // X-Correlation-Id is by construction the id that lands in the row. One id across both
        // deployments' logs is the whole point.
        var context = new CallContext(Tenant, Connection, "CreateCustomer");
        var headerValue = context.Correlation;

        await Journal().RecordAsync(
            context, _ => Task.FromResult(IntegrationResult.Ok()), CancellationToken.None);

        headerValue.Should().NotBeNullOrWhiteSpace();
        _store.Rows.Should().ContainSingle().Which.CorrelationId.Should().Be(headerValue);
        CallJournal.CorrelationHeader.Should().Be("X-Correlation-Id");
    }

    [Fact]
    public void A_generated_correlation_id_is_stable_across_reads_but_unique_per_call()
    {
        var first = new CallContext(Tenant, Connection, "CreateCustomer");
        var second = new CallContext(Tenant, Connection, "CreateCustomer");

        first.Correlation.Should().Be(first.Correlation, "the header and the row must agree");
        first.Correlation.Should().NotBe(second.Correlation, "two calls are two support tickets");
    }

    [Fact]
    public async Task An_upstream_correlation_id_is_preserved_rather_than_replaced()
    {
        var context = new CallContext(
            Tenant, Connection, "CreateCustomer", CorrelationId: "  cmd-7f3a  ");

        await Journal().RecordAsync(
            context, _ => Task.FromResult(IntegrationResult.Ok()), CancellationToken.None);

        _store.Rows.Should().ContainSingle().Which.CorrelationId.Should().Be("cmd-7f3a");
    }

    [Fact]
    public async Task A_cloned_context_takes_the_new_correlation_id_and_not_the_stale_generated_one()
    {
        // Guards the subtlety of the record's copy constructor: it copies the generated fallback
        // field verbatim, so `Correlation` has to be derived from the property rather than read
        // from that field.
        var original = new CallContext(Tenant, Connection, "CreateCustomer");
        var clone = original with { CorrelationId = "cmd-upstream" };

        await Journal().RecordAsync(
            clone, _ => Task.FromResult(IntegrationResult.Ok()), CancellationToken.None);

        clone.Correlation.Should().Be("cmd-upstream");
        _store.Rows.Should().ContainSingle().Which.CorrelationId.Should().Be("cmd-upstream");
    }

    [Fact]
    public async Task Records_the_http_status_the_transport_observed()
    {
        var context = new CallContext(Tenant, Connection, "GetBalance");

        await Journal().RecordAsync(
            context,
            _ =>
            {
                // What an HTTP adapter does as soon as it has a response.
                context.Probe.HttpStatus = 503;
                return Task.FromResult(IntegrationResult.Transient(IntegrationErrors.Unavailable));
            },
            CancellationToken.None);

        _store.Rows.Should().ContainSingle().Which.HttpStatus.Should().Be(503);
    }

    [Fact]
    public async Task Records_the_command_a_call_served_and_null_for_a_read()
    {
        var commandId = Guid.NewGuid();
        var journal = Journal();

        await journal.RecordAsync(
            new CallContext(Tenant, Connection, "CreateCustomer", CommandId: commandId),
            _ => Task.FromResult(IntegrationResult.Ok()), CancellationToken.None);

        await journal.RecordAsync(
            new CallContext(Tenant, Connection, "GetBalance"),
            _ => Task.FromResult(IntegrationResult.Ok()), CancellationToken.None);

        _store.Rows[0].CommandId.Should().Be(commandId);
        _store.Rows[1].CommandId.Should().BeNull("a live balance read has no command behind it");
    }

    [Fact]
    public async Task A_blank_operation_fails_loudly_instead_of_losing_the_row_silently()
    {
        var act = async () => await Journal().RecordAsync(
            new CallContext(Tenant, Connection, "   "),
            _ => Task.FromResult(IntegrationResult.Ok()),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>(
            "the alternative is a DomainException swallowed with every other write failure, and an "
            + "adapter author who only sees a missing row");

        _store.Rows.Should().BeEmpty("and the operation is not even attempted");
    }

    /// <summary>Captures what the journal appended, and with which token.</summary>
    private sealed class RecordingStore : ICallLogStore
    {
        internal List<IntegrationCallLog> Rows { get; } = [];

        internal CancellationToken LastToken { get; private set; }

        public Task AppendAsync(IntegrationCallLog row, CancellationToken ct)
        {
            Rows.Add(row);
            LastToken = ct;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A frozen clock. Local rather than from a package: the module's tests take no dependency on
    /// Microsoft.Extensions.TimeProvider.Testing, and the journal only ever reads the time once.
    /// </summary>
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

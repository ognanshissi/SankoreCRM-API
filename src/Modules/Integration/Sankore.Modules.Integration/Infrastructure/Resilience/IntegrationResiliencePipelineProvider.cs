namespace Sankore.Modules.Integration.Infrastructure.Resilience;

using System.Collections.Concurrent;
using System.Net;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Sankore.Modules.Integration.Domain;

/// <summary>
/// Per-connection Polly v8 pipelines with an <b>observable</b> circuit breaker (INT-09).
///
/// <para>
/// <b>Why this exists instead of <c>AddStandardResilienceHandler</c>.</b> The standard handler
/// gives timeout, retry and breaker in one line — and exposes no
/// <see cref="CircuitBreakerStateProvider"/>. INT-09's fourth criterion is that the breaker state
/// appears in the module's health check, so the state provider has to be ours, kept per
/// connection and readable afterwards. There is no way to reach into the standard handler's
/// breaker; the pipeline has to be built explicitly.
/// </para>
///
/// <para>
/// <b>Read and write differ, and the difference is the point.</b> Criterion 1 says a short retry
/// <i>on direct reads only</i>. A write is already retried by the dispatcher
/// (<c>IntegrationCommand.ScheduleRetry</c> + the attempt budget), and retrying it a second time
/// inside the HTTP pipeline would double-send: an account opening that timed out after the CBS
/// committed it comes back as a duplicate, and the customer owns two accounts. A balance read is
/// idempotent and nobody is watching a spinner for thirty seconds, so it gets two quick retries.
/// </para>
///
/// <para>
/// <b>One breaker per connection, shared by both pipelines.</b> The breaker's job is to answer
/// "is this CBS up", which is a fact about the connection and not about the verb. A read pipeline
/// with its own breaker would keep hammering a system whose writes have already been cut off, and
/// the health check would have two states to reconcile for one row. A
/// <see cref="CircuitBreakerStateProvider"/> can be bound to exactly one strategy instance, so
/// the breaker is built once as its own pipeline and composed into both.
/// </para>
///
/// <para>
/// <b>No <c>SsrfSafeHandler</c>, deliberately.</b> M13's handler is the right default for a URL a
/// tenant typed into a lead-source form, and it is wrong here: it refuses every RFC 1918 address,
/// and a core banking system is frequently on-premise at <c>10.x</c> or <c>192.168.x</c> — the
/// perfectly ordinary case this module exists to serve. It is opt-in per HttpClient, so the
/// correct action is simply never to call it. An integration connection's base URL is configured
/// by an administrator of the deployment, not supplied by a caller, so the threat it defends
/// against is not present. Do not add it here.
/// </para>
/// </summary>
internal sealed class IntegrationResiliencePipelineProvider
{
    /// <summary>Consecutive failures that open the circuit when the connection says nothing.</summary>
    internal const int DefaultFailureThreshold = 5;

    /// <summary>How long the circuit stays open when the connection says nothing.</summary>
    internal const int DefaultBreakSeconds = 30;

    /// <summary>Per-call budget when the connection says nothing.</summary>
    internal const int DefaultTimeoutSeconds = 30;

    /// <summary>Retries a direct read gets. Short: a human is waiting on the other end.</summary>
    internal const int ReadRetryAttempts = 2;

    private readonly ConcurrentDictionary<Guid, ConnectionPipelines> _byConnection = new();

    /// <summary>
    /// The pipeline a write goes through: breaker then timeout, no retry. See the class remarks
    /// for why the retry is the dispatcher's and not this pipeline's.
    /// </summary>
    public ResiliencePipeline<HttpResponseMessage> GetWritePipeline(IntegrationConnection connection)
        => Pipelines(connection).Write;

    /// <summary>The pipeline a direct read goes through: short retry, then breaker, then timeout.</summary>
    public ResiliencePipeline<HttpResponseMessage> GetReadPipeline(IntegrationConnection connection)
        => Pipelines(connection).Read;

    /// <summary>
    /// The breaker's state for one connection, or <c>null</c> when no call has ever been made
    /// through it in this process. Null is not "healthy": it is "unknown", and
    /// <see cref="IntegrationHealthCheck"/> reports it as such rather than as a closed circuit.
    /// </summary>
    public CircuitState? GetCircuitState(Guid connectionId)
        => _byConnection.TryGetValue(connectionId, out var entry)
            ? entry.BreakerState.CircuitState
            : null;

    /// <summary>
    /// Every connection this process has built a pipeline for, with its current circuit state.
    /// Read by the health check; a snapshot, so an entry added mid-enumeration is simply missed.
    /// </summary>
    public IReadOnlyDictionary<Guid, CircuitState> CircuitStates
        => _byConnection.ToDictionary(e => e.Key, e => e.Value.BreakerState.CircuitState);

    private ConnectionPipelines Pipelines(IntegrationConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var version = connection.Version;

        // Cached by connection id, rebuilt when the row's xmin moves. An administrator who
        // lowers the failure threshold is asking for the new threshold to apply, and keeping the
        // first pipeline for the life of the process would make the setting look inert. The
        // breaker state resets with it, which is the honest consequence: the thresholds that
        // produced the old state no longer exist.
        //
        // Replaced rather than accumulated, so the dictionary stays one entry per connection
        // however many times its settings are edited.
        if (_byConnection.TryGetValue(connection.Id, out var existing) && existing.Version == version)
            return existing;

        var built = Build(connection, version);
        _byConnection[connection.Id] = built;
        return built;
    }

    private static ConnectionPipelines Build(IntegrationConnection connection, uint version)
    {
        var settings = connection.Settings;

        var timeout = TimeSpan.FromSeconds(
            Positive(settings?.TimeoutSeconds, DefaultTimeoutSeconds));

        var breakSeconds = Positive(settings?.CircuitBreakerBreakSeconds, DefaultBreakSeconds);

        // Polly v8 has no consecutive-failure breaker: it samples a ratio over a window. The
        // closest expression of "N consecutive failures" is a ratio of 1.0 with a minimum
        // throughput of N — the circuit opens only when every one of the last N calls inside the
        // sampling window failed. MinimumThroughput has a floor of 2 in Polly, so a connection
        // configured with 1 gets 2; a breaker that opens on a single blip is not a breaker.
        var threshold = Math.Max(2, Positive(settings?.CircuitBreakerFailureThreshold, DefaultFailureThreshold));

        // The window must outlast the break, or a circuit that half-opens finds an empty sample
        // and can never re-open on the trial call's failure alone.
        var sampling = TimeSpan.FromSeconds(Math.Max(30, breakSeconds * 2));

        var breakerState = new CircuitBreakerStateProvider();

        var breaker = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = TransientPredicate(),
                FailureRatio = 1.0,
                MinimumThroughput = threshold,
                SamplingDuration = sampling,
                BreakDuration = TimeSpan.FromSeconds(breakSeconds),
                StateProvider = breakerState,
            })
            .Build();

        // Order is outermost-first. Timeout sits INSIDE the breaker on purpose: a call that blew
        // its budget is exactly the evidence the breaker exists to count, and a timeout outside
        // it would hide every slow failure from the state the health check reports.
        var write = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddPipeline(breaker)
            .AddTimeout(timeout)
            .Build();

        // Retry outermost, so each of its attempts is a fresh trip through breaker and timeout —
        // and so an open circuit fails the attempts instantly instead of waiting out the budget
        // three times.
        var read = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = TransientPredicate(),
                MaxRetryAttempts = ReadRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(200),
                UseJitter = true,
            })
            .AddPipeline(breaker)
            .AddTimeout(timeout)
            .Build();

        return new ConnectionPipelines(version, read, write, breakerState);
    }

    /// <summary>
    /// What counts as transient, for both the retry and the breaker, and nothing else does.
    ///
    /// <para>
    /// A 4xx is absent on purpose: a 400, a 403 or a 404 is the external system answering on the
    /// merits, and counting it towards the breaker would let one tenant's bad mapping cut off
    /// every other call on the connection. 408 and 429 are the two exceptions — both mean "ask
    /// again", not "no".
    /// </para>
    ///
    /// <para>
    /// A <see cref="PredicateBuilder{TResult}"/> rather than a lambda because Polly converts it
    /// implicitly to both a retry predicate and a breaker predicate — one definition, two
    /// strategies, no chance of the retry and the breaker disagreeing about what "transient" is.
    /// </para>
    /// </summary>
    private static PredicateBuilder<HttpResponseMessage> TransientPredicate()
        => new PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>()
            .Handle<TimeoutRejectedException>()
            .Handle<TaskCanceledException>()
            .HandleResult(IsTransient);

    private static bool IsTransient(HttpResponseMessage response)
        => (int)response.StatusCode >= 500
            || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    /// <summary>Zero or absent means "take the default", as every field of ConnectionSettings says.</summary>
    private static int Positive(int? configured, int fallback)
        => configured is > 0 ? configured.Value : fallback;

    private sealed record ConnectionPipelines(
        uint Version,
        ResiliencePipeline<HttpResponseMessage> Read,
        ResiliencePipeline<HttpResponseMessage> Write,
        CircuitBreakerStateProvider BreakerState);
}

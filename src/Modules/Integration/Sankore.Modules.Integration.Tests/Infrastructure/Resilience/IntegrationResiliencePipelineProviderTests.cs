namespace Sankore.Modules.Integration.Tests.Infrastructure.Resilience;

using System.Net;
using FluentAssertions;
using Polly.CircuitBreaker;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure.Resilience;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// INT-09 criterion 1: timeout, a short retry on direct reads ONLY, and a circuit breaker whose
/// thresholds come from the connection and whose state can be read back (which is what criterion 4
/// needs and what <c>AddStandardResilienceHandler</c> cannot give).
/// </summary>
public sealed class IntegrationResiliencePipelineProviderTests
{
    [Fact]
    public void The_circuit_state_is_unknown_until_a_pipeline_has_been_built()
    {
        var provider = new IntegrationResiliencePipelineProvider();

        // Null is "no call has gone out in this process", not "closed". Reporting it as Closed
        // would tell an operator the opposite of the truth.
        provider.GetCircuitState(Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public async Task Opens_after_the_configured_number_of_consecutive_failures()
    {
        var provider = new IntegrationResiliencePipelineProvider();
        var connection = Connection(failureThreshold: 2, breakSeconds: 30);

        var pipeline = provider.GetWritePipeline(connection);

        // Two 500s: the far end is down, not refusing on the merits.
        for (var i = 0; i < 2; i++)
        {
            var response = await pipeline.ExecuteAsync(
                _ => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }

        provider.GetCircuitState(connection.Id).Should().Be(CircuitState.Open);

        var thirdCall = async () => await pipeline.ExecuteAsync(
            _ => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        await thirdCall.Should().ThrowAsync<BrokenCircuitException>();
    }

    [Fact]
    public async Task A_business_refusal_does_not_count_towards_the_breaker()
    {
        var provider = new IntegrationResiliencePipelineProvider();
        var connection = Connection(failureThreshold: 2, breakSeconds: 30);

        var pipeline = provider.GetWritePipeline(connection);

        // A 400 is the external system answering on the merits. Counting it would let one tenant's
        // bad mapping cut off every other call on the connection.
        for (var i = 0; i < 4; i++)
        {
            await pipeline.ExecuteAsync(
                _ => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
        }

        provider.GetCircuitState(connection.Id).Should().Be(CircuitState.Closed);
    }

    [Fact]
    public async Task A_read_is_retried_and_a_write_is_not()
    {
        var provider = new IntegrationResiliencePipelineProvider();

        // A high threshold so the breaker stays out of the way of what this test measures.
        var connection = Connection(failureThreshold: 20, breakSeconds: 30);

        var readCalls = 0;
        await provider.GetReadPipeline(connection).ExecuteAsync(_ =>
        {
            readCalls++;
            return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });

        var writeCalls = 0;
        await provider.GetWritePipeline(connection).ExecuteAsync(_ =>
        {
            writeCalls++;
            return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });

        readCalls.Should().Be(1 + IntegrationResiliencePipelineProvider.ReadRetryAttempts);

        // A write is retried by the dispatcher's own budget. Retrying it here too would
        // double-send: an account opening that timed out after the CBS committed it comes back as
        // a duplicate, and the customer owns two accounts.
        writeCalls.Should().Be(1);
    }

    [Fact]
    public void One_breaker_per_connection_shared_by_both_pipelines()
    {
        var provider = new IntegrationResiliencePipelineProvider();
        var connection = Connection(failureThreshold: 3, breakSeconds: 30);

        var firstRead = provider.GetReadPipeline(connection);
        var secondRead = provider.GetReadPipeline(connection);
        var write = provider.GetWritePipeline(connection);

        // Cached, so the breaker's count is not reset by whoever asks for a pipeline next.
        firstRead.Should().BeSameAs(secondRead);

        // And a distinct pipeline for writes, because only one of the two retries.
        write.Should().NotBeSameAs(firstRead);
    }

    [Fact]
    public async Task Breaker_state_is_tracked_per_connection()
    {
        var provider = new IntegrationResiliencePipelineProvider();

        var failing = Connection(failureThreshold: 2, breakSeconds: 30);
        var healthy = Connection(failureThreshold: 2, breakSeconds: 30);

        for (var i = 0; i < 2; i++)
        {
            await provider.GetWritePipeline(failing).ExecuteAsync(
                _ => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        }

        await provider.GetWritePipeline(healthy).ExecuteAsync(
            _ => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        provider.CircuitStates.Should().HaveCount(2);
        provider.CircuitStates[failing.Id].Should().Be(CircuitState.Open);
        provider.CircuitStates[healthy.Id].Should().Be(CircuitState.Closed);
    }

    [Fact]
    public async Task A_timeout_counts_as_a_failure_the_breaker_can_see()
    {
        var provider = new IntegrationResiliencePipelineProvider();

        // The timeout sits INSIDE the breaker on purpose: a call that blew its budget is exactly
        // the evidence the breaker exists to count.
        var connection = Connection(failureThreshold: 2, breakSeconds: 30, timeoutSeconds: 1);

        var pipeline = provider.GetWritePipeline(connection);

        for (var i = 0; i < 2; i++)
        {
            var call = async () => await pipeline.ExecuteAsync(
                async ct =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                });

            await call.Should().ThrowAsync<Exception>();
        }

        provider.GetCircuitState(connection.Id).Should().Be(CircuitState.Open);
    }

    internal static IntegrationConnection Connection(
        int failureThreshold,
        int breakSeconds,
        int timeoutSeconds = 30,
        int rateLimitPerMinute = 0,
        Guid? id = null)
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 3, 11, 9, 0, 0, TimeSpan.Zero));

        return IntegrationConnection.Create(
            tenantId: Guid.Parse("ffffffff-0000-0000-0000-000000000006"),
            family: IntegrationFamily.CoreBanking,
            kind: IntegrationKind.Fake,
            mode: IntegrationMode.Api,
            name: "Fake CBS",
            settings: new FakeSettings
            {
                CircuitBreakerFailureThreshold = failureThreshold,
                CircuitBreakerBreakSeconds = breakSeconds,
                TimeoutSeconds = timeoutSeconds,
                RateLimitPerMinute = rateLimitPerMinute,
            },
            createdBy: Guid.NewGuid(),
            clock: clock,
            id: id ?? Guid.NewGuid());
    }

    internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

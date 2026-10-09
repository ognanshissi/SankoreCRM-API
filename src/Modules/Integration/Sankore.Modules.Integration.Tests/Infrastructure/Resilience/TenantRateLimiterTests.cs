namespace Sankore.Modules.Integration.Tests.Infrastructure.Resilience;

using FluentAssertions;
using Sankore.Modules.Integration.Infrastructure.Resilience;
using Xunit;

/// <summary>
/// INT-09 criterion 2: per-tenant outbound rate limiting, configured in the connection's settings.
/// </summary>
public sealed class TenantRateLimiterTests
{
    private static readonly Guid TenantA = Guid.Parse("11111111-aaaa-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("22222222-bbbb-0000-0000-000000000002");

    [Fact]
    public void Refuses_past_the_configured_rate()
    {
        using var limiter = new TenantRateLimiter();
        var connection = Guid.NewGuid();

        limiter.TryAcquire(TenantA, connection, permitsPerMinute: 2).Should().BeTrue();
        limiter.TryAcquire(TenantA, connection, permitsPerMinute: 2).Should().BeTrue();

        // The window is a minute and nothing queues: a refusal becomes
        // INTEGRATION_RATE_LIMITED — a transient failure, so the dispatcher's backoff carries the
        // command to the next window rather than holding a Hangfire worker idle.
        limiter.TryAcquire(TenantA, connection, permitsPerMinute: 2).Should().BeFalse();
    }

    [Fact]
    public void Zero_means_unlimited()
    {
        using var limiter = new TenantRateLimiter();
        var connection = Guid.NewGuid();

        // Most CBS contracts set no ceiling at all, so this is the common path and it must not
        // pay for a window nothing ever closes.
        for (var i = 0; i < 500; i++)
            limiter.TryAcquire(TenantA, connection, permitsPerMinute: 0).Should().BeTrue();
    }

    [Fact]
    public void One_tenant_cannot_eat_anothers_allowance()
    {
        using var limiter = new TenantRateLimiter();
        var connection = Guid.NewGuid();

        limiter.TryAcquire(TenantA, connection, permitsPerMinute: 1).Should().BeTrue();
        limiter.TryAcquire(TenantA, connection, permitsPerMinute: 1).Should().BeFalse();

        limiter.TryAcquire(TenantB, connection, permitsPerMinute: 1).Should().BeTrue();
    }

    [Fact]
    public void Each_connection_of_a_tenant_has_its_own_window()
    {
        using var limiter = new TenantRateLimiter();

        var cbs = Guid.NewGuid();
        var insurer = Guid.NewGuid();

        limiter.TryAcquire(TenantA, cbs, permitsPerMinute: 1).Should().BeTrue();
        limiter.TryAcquire(TenantA, cbs, permitsPerMinute: 1).Should().BeFalse();

        // The limit belongs to the far end — what that system's licence allows — so a saturated
        // CBS must not stop the tenant's insurance calls.
        limiter.TryAcquire(TenantA, insurer, permitsPerMinute: 1).Should().BeTrue();
    }

    [Fact]
    public void Reads_the_ceiling_off_the_connection()
    {
        using var limiter = new TenantRateLimiter();

        var connection = IntegrationResiliencePipelineProviderTests.Connection(
            failureThreshold: 5, breakSeconds: 30, rateLimitPerMinute: 1);

        limiter.TryAcquire(TenantA, connection).Should().BeTrue();
        limiter.TryAcquire(TenantA, connection).Should().BeFalse();
    }

    [Fact]
    public void Raising_a_ceiling_takes_effect_at_once()
    {
        using var limiter = new TenantRateLimiter();
        var connection = Guid.NewGuid();

        limiter.TryAcquire(TenantA, connection, permitsPerMinute: 1).Should().BeTrue();
        limiter.TryAcquire(TenantA, connection, permitsPerMinute: 1).Should().BeFalse();

        // The configured rate is part of the partition key, so an operator who negotiated a
        // higher ceiling does not have to wait out the old window.
        limiter.TryAcquire(TenantA, connection, permitsPerMinute: 10).Should().BeTrue();
    }
}

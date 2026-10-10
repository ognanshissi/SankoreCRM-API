namespace Sankore.Modules.Integration.Tests.Infrastructure.Resilience;

using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.Resilience;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// INT-09 criterion 4: the breaker state reaches the module's health check.
/// </summary>
public sealed class IntegrationHealthCheckTests
{
    private static readonly Guid Tenant = Guid.Parse("ffffffff-0000-0000-0000-000000000006");
    private static readonly DateTimeOffset Now = new(2026, 3, 11, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Healthy_and_explicit_when_no_connection_is_configured()
    {
        await using var db = NewDb();
        var check = new IntegrationHealthCheck(db, new IntegrationResiliencePipelineProvider());

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        // An IMF that has not bought a CBS link is not a broken deployment.
        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("No active integration connection");
    }

    [Fact]
    public async Task Reports_an_open_circuit_as_unhealthy_and_names_the_connection()
    {
        await using var db = NewDb();

        var connection = Active(failureThreshold: 2);
        db.Connections.Add(connection);
        await db.SaveChangesAsync();

        var pipelines = new IntegrationResiliencePipelineProvider();

        for (var i = 0; i < 2; i++)
        {
            await pipelines.GetWritePipeline(connection).ExecuteAsync(
                _ => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        }

        var result = await new IntegrationHealthCheck(db, pipelines)
            .CheckHealthAsync(new HealthCheckContext());

        // Writes to that CBS are being refused right now, which is the one state that deserves
        // Unhealthy.
        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data.Should().ContainKey($"connection:{connection.Id}");
        result.Description.Should().Contain("1 with an open circuit");
    }

    [Fact]
    public async Task Reports_a_failed_stored_health_result_as_degraded()
    {
        await using var db = NewDb();

        var connection = Active(failureThreshold: 5);
        connection.RecordHealth(
            IntegrationHealth.Unhealthy("the relay agent is not connected", Now), Clock);

        db.Connections.Add(connection);
        await db.SaveChangesAsync();

        var result = await new IntegrationHealthCheck(db, new IntegrationResiliencePipelineProvider())
            .CheckHealthAsync(new HealthCheckContext());

        // Something is wrong but the platform still functions: a command that cannot be sent is
        // queued, not lost.
        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task Healthy_when_every_connection_is_up_and_no_breaker_has_opened()
    {
        await using var db = NewDb();

        var connection = Active(failureThreshold: 5);
        db.Connections.Add(connection);
        await db.SaveChangesAsync();

        var pipelines = new IntegrationResiliencePipelineProvider();
        await pipelines.GetWritePipeline(connection).ExecuteAsync(
            _ => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var result = await new IntegrationHealthCheck(db, pipelines)
            .CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Does_not_report_a_never_used_breaker_as_closed()
    {
        await using var db = NewDb();

        var connection = Active(failureThreshold: 5);
        db.Connections.Add(connection);
        await db.SaveChangesAsync();

        var result = await new IntegrationHealthCheck(db, new IntegrationResiliencePipelineProvider())
            .CheckHealthAsync(new HealthCheckContext());

        // No call has gone out since the last restart. "Unknown" and not "Closed": a breaker that
        // has never run is not evidence of health.
        result.Data[$"connection:{connection.Id}"].ToString()
            .Should().Contain("Unknown");
    }

    [Fact]
    public async Task Spans_tenants_because_the_health_endpoint_carries_no_jwt()
    {
        // The context is built for one tenant; the connection belongs to it, and the check is
        // read by an operator asking "is any connection down". IgnoreQueryFilters is what makes
        // that answerable, and the tenant id is published per row so the answer is actionable.
        await using var db = NewDb(ambientTenant: Guid.NewGuid());

        var connection = Active(failureThreshold: 5);
        db.Connections.Add(connection);
        await db.SaveChangesAsync();

        var result = await new IntegrationHealthCheck(db, new IntegrationResiliencePipelineProvider())
            .CheckHealthAsync(new HealthCheckContext());

        result.Data.Should().ContainKey($"connection:{connection.Id}");
    }

    [Fact]
    public async Task Ignores_a_deactivated_connection()
    {
        await using var db = NewDb();

        var connection = Active(failureThreshold: 5);
        connection.Deactivate(Guid.NewGuid(), Clock);

        db.Connections.Add(connection);
        await db.SaveChangesAsync();

        var result = await new IntegrationHealthCheck(db, new IntegrationResiliencePipelineProvider())
            .CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().BeEmpty();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static TimeProvider Clock => new FixedClock(Now);

    private static IntegrationDbContext NewDb(Guid? ambientTenant = null)
        => new(
            new DbContextOptionsBuilder<IntegrationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            new FixedTenantContext(ambientTenant ?? Tenant));

    private static IntegrationConnection Active(int failureThreshold)
    {
        var connection = IntegrationResiliencePipelineProviderTests.Connection(
            failureThreshold, breakSeconds: 30);

        // Activation requires a passed health check (INT-03), so the healthy result comes first.
        connection.RecordHealth(IntegrationHealth.Healthy(TimeSpan.FromMilliseconds(80), Now), Clock);
        connection.Activate(Guid.NewGuid(), Clock).IsSuccess.Should().BeTrue();

        return connection;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

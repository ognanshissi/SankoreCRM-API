namespace Sankore.Modules.Integration.Tests.Features.CallLog;

using System.Globalization;
using FluentAssertions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.CallLog.GetCallLogStats;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// <c>GET integration/call-log/stats</c> — the endpoint that answers "is the CBS slow today".
///
/// <para>
/// The number under test is the p95, because it is the one that makes the journal usable. A CBS
/// answering ninety calls in 40 ms and ten in 30 s averages three seconds — a figure that
/// describes nobody's experience, and that hides the ten agents who waited half a minute. Only
/// the tail shows them. So the fixtures below are deliberately skewed distributions rather than
/// round numbers, and the rank is asserted exactly.
/// </para>
/// </summary>
public sealed class GetCallLogStatsHandlerTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenant = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid ConnectionA = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ConnectionB = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-03-20T12:00:00Z", CultureInfo.InvariantCulture);

    /// <summary>Expected ordering of <c>Groups_per_operation_busiest_first</c>.</summary>
    private static readonly string[] BusiestFirst = ["GetBalance", "CreateCustomer"];

    [Fact]
    public async Task The_p95_is_the_tail_and_not_the_average()
    {
        // 90 fast calls and 10 slow ones: the shape of a back-office that is degrading, and the
        // shape an average is blind to. Ten and not five, because nearest-rank is honest: with
        // only five slow calls out of a hundred, the 95th ascending value IS a fast one and the
        // p95 correctly reports 40 ms — 95 % of callers really were served quickly. The tail has
        // to be wider than the complement of the percentile before the percentile shows it, and
        // that is a property of the statistic, not a bug to tune away.
        var durations = Enumerable.Repeat(40L, 90).Concat(Enumerable.Repeat(30_000L, 10)).ToArray();

        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await SeedAsync(factory, [.. durations.Select(d =>
            Row(Tenant, ConnectionA, "OpenAccount", Now.AddHours(-1), durationMs: d))]);

        var stats = (await HandleAsync(factory, Query())).Value;

        var operation = stats.Operations.Should().ContainSingle().Which;

        operation.Calls.Should().Be(100);
        operation.AvgDurationMs.Should().Be(
            3_036, "three seconds, which is nobody's experience: ninety callers waited 40 ms");
        operation.P95DurationMs.Should().Be(
            30_000,
            "nearest-rank: the 95th of 100 ascending durations falls inside the slow tail, which "
            + "is what the ten who waited half a minute will be complaining about");
        operation.MaxDurationMs.Should().Be(30_000);
    }

    [Fact]
    public async Task The_p95_is_an_observed_duration_and_never_an_interpolation()
    {
        // Ten calls: ceil(0.95 × 10) = 10, so the p95 is the 10th ascending value. An interpolated
        // percentile would answer 955 here; a nearest-rank one answers a duration that can be
        // looked up as an actual row.
        var durations = new long[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 1_000 };

        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await SeedAsync(factory, [.. durations.Select(d =>
            Row(Tenant, ConnectionA, "GetBalance", Now.AddHours(-1), durationMs: d))]);

        var operation = (await HandleAsync(factory, Query())).Value.Operations.Single();

        operation.P95DurationMs.Should().Be(1_000);
        durations.Should().Contain(operation.P95DurationMs);
    }

    [Fact]
    public async Task A_single_call_is_its_own_p95()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await SeedAsync(factory, Row(Tenant, ConnectionA, "CheckHealth", Now.AddHours(-1), durationMs: 77));

        var operation = (await HandleAsync(factory, Query())).Value.Operations.Single();

        operation.Calls.Should().Be(1);
        operation.P95DurationMs.Should().Be(77);
        operation.AvgDurationMs.Should().Be(77);
    }

    [Fact]
    public async Task Counts_are_broken_out_per_error_family_because_each_means_a_different_action()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await SeedAsync(factory,
            Row(Tenant, ConnectionA, "CreateCustomer", Now.AddHours(-1)),
            Row(Tenant, ConnectionA, "CreateCustomer", Now.AddHours(-1),
                family: ErrorFamily.Transient, code: IntegrationErrors.Timeout),
            Row(Tenant, ConnectionA, "CreateCustomer", Now.AddHours(-1),
                family: ErrorFamily.Transient, code: IntegrationErrors.Unavailable),
            Row(Tenant, ConnectionA, "CreateCustomer", Now.AddHours(-1),
                family: ErrorFamily.Functional, code: IntegrationErrors.Duplicate),
            Row(Tenant, ConnectionA, "CreateCustomer", Now.AddHours(-1),
                family: ErrorFamily.Technical, code: IntegrationErrors.MappingMissing));

        var stats = (await HandleAsync(factory, Query())).Value;
        var operation = stats.Operations.Single();

        operation.Calls.Should().Be(5);
        operation.Failures.Should().Be(4);
        operation.TransientFailures.Should().Be(2);
        operation.FunctionalFailures.Should().Be(1);
        operation.TechnicalFailures.Should().Be(1);

        stats.TotalCalls.Should().Be(5);
        stats.FailedCalls.Should().Be(4);
    }

    [Fact]
    public async Task Groups_per_operation_busiest_first()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await SeedAsync(factory,
            Row(Tenant, ConnectionA, "GetBalance", Now.AddHours(-1)),
            Row(Tenant, ConnectionA, "GetBalance", Now.AddHours(-1)),
            Row(Tenant, ConnectionA, "GetBalance", Now.AddHours(-1)),
            Row(Tenant, ConnectionA, "CreateCustomer", Now.AddHours(-1)));

        var operations = (await HandleAsync(factory, Query())).Value.Operations;

        operations.Select(o => o.Operation).Should().Equal(
            BusiestFirst,
            "a CBS is rarely slow as a whole: one endpoint degrades, and the busiest is where to "
            + "look first");
    }

    [Fact]
    public async Task An_operation_with_no_call_in_the_window_is_absent_rather_than_zero()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await SeedAsync(factory,
            Row(Tenant, ConnectionA, "Recent", Now.AddHours(-1)),
            Row(Tenant, ConnectionA, "Ancient", Now.AddDays(-40)));

        var operations = (await HandleAsync(factory, Query())).Value.Operations;

        operations.Should().ContainSingle().Which.Operation.Should().Be(
            "Recent",
            "this module cannot enumerate the operations an adapter supports — the capability "
            + "matrix is per installation — so a zero row would be an invention");
    }

    [Fact]
    public async Task Defaults_to_the_same_seven_day_window_as_the_list()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await SeedAsync(factory, Row(Tenant, ConnectionA, "CreateCustomer", Now.AddHours(-1)));

        var stats = (await HandleAsync(factory, Query())).Value;

        stats.From.Should().Be(
            Now.AddDays(-7),
            "a list defaulting to seven days next to statistics defaulting to one would have an "
            + "operator comparing a p95 against a different period, with neither screen saying so");
        stats.To.Should().Be(Now);
    }

    [Fact]
    public async Task Narrows_to_one_connection()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await SeedAsync(factory,
            Row(Tenant, ConnectionA, "CreateCustomer", Now.AddHours(-1), durationMs: 10),
            Row(Tenant, ConnectionB, "CreateCustomer", Now.AddHours(-1), durationMs: 5_000));

        var operation = (await HandleAsync(factory, Query() with { ConnectionId = ConnectionA }))
            .Value.Operations.Single();

        operation.Calls.Should().Be(1);
        operation.P95DurationMs.Should().Be(10, "the slow connection must not pollute this one's tail");
    }

    [Fact]
    public async Task Another_tenants_connection_id_yields_no_operations_rather_than_a_refusal()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await SeedAsync(factory,
            Row(Tenant, ConnectionA, "Mine", Now.AddHours(-1)),
            Row(OtherTenant, ConnectionB, "Theirs", Now.AddHours(-1)));

        var result = await HandleAsync(factory, Query() with { ConnectionId = ConnectionB });

        result.IsSuccess.Should().BeTrue();
        result.Value.Operations.Should().BeEmpty();
        result.Value.TotalCalls.Should().Be(0);
    }

    [Fact]
    public async Task An_unknown_error_family_is_refused_by_name()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await SeedAsync(factory, Row(Tenant, ConnectionA, "CreateCustomer", Now.AddHours(-1)));

        var result = await HandleAsync(factory, Query() with { ErrorFamily = "Slow" });

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("INTEGRATION_ERROR_FAMILY_UNKNOWN");
    }

    [Fact]
    public async Task An_empty_window_is_a_success_with_nothing_in_it()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);

        var result = await HandleAsync(factory, Query());

        result.IsSuccess.Should().BeTrue("no calls is an answer, not a 404");
        result.Value.Operations.Should().BeEmpty();
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private static GetCallLogStatsQuery Query() => new(null, null, null, null);

    private static async Task<Result<CallLogStats>> HandleAsync(
        TestIntegrationDbContextFactory factory, GetCallLogStatsQuery query)
    {
        await using var db = factory.CreateContext();
        var handler = new GetCallLogStatsHandler(db, new FrozenClock(Now));
        return await handler.Handle(query, CancellationToken.None);
    }

    private static async Task SeedAsync(
        TestIntegrationDbContextFactory factory, params IntegrationCallLog[] rows)
    {
        await using var db = factory.CreateContext();
        db.CallLogs.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static IntegrationCallLog Row(
        Guid tenantId,
        Guid connectionId,
        string operation,
        DateTimeOffset at,
        ErrorFamily? family = null,
        string? code = null,
        long durationMs = 100) =>
        IntegrationCallLog.Record(
            tenantId: tenantId,
            connectionId: connectionId,
            operation: operation,
            durationMs: durationMs,
            at: at,
            endpoint: "/party/v2/customers",
            errorFamily: family,
            errorCode: code,
            correlationId: "corr-1");

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

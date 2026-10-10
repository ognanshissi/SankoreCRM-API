namespace Sankore.Modules.Integration.Tests.Features.CallLog;

using System.Globalization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.CallLog.ListCallLog;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The read side of the journal: <c>GET integration/call-log</c>.
///
/// <para>
/// The behaviour worth pinning is the default window. A screen with no date filter is the normal
/// way this endpoint is first opened, and the table is <c>PARTITION BY RANGE (at)</c> — so
/// "no filter" has to mean a bounded period rather than a scan of every partition the deployment
/// has accumulated. The default is also what makes the result surprising if it is invisible,
/// which is why the resolved window comes back in the response.
/// </para>
/// </summary>
public sealed class ListCallLogHandlerTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenant = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid ConnectionA = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ConnectionB = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-03-20T12:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Defaults_to_the_last_seven_days_and_says_so()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await SeedAsync(factory,
            Row(Tenant, ConnectionA, "CreateCustomer", Now.AddDays(-1)),
            Row(Tenant, ConnectionA, "CreateCustomer", Now.AddDays(-30)));

        var result = await HandleAsync(factory, Query());

        result.IsSuccess.Should().BeTrue();
        result.Value.From.Should().Be(
            Now.AddDays(-7),
            "an unbounded query over a partitioned table is planned across every partition; the "
            + "default is a window, not a refusal");
        result.Value.To.Should().Be(Now);

        result.Value.Rows.Should().HaveCount(1, "the 30-day-old call is outside the default window");
    }

    [Fact]
    public async Task An_explicit_window_is_honoured_in_full()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await SeedAsync(factory,
            Row(Tenant, ConnectionA, "CreateCustomer", Now.AddDays(-1)),
            Row(Tenant, ConnectionA, "CreateCustomer", Now.AddDays(-30)));

        var result = await HandleAsync(factory, Query() with { From = Now.AddDays(-60), To = Now });

        result.Value.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Newest_first()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await SeedAsync(factory,
            Row(Tenant, ConnectionA, "Older", Now.AddHours(-5)),
            Row(Tenant, ConnectionA, "Newer", Now.AddHours(-1)));

        var result = await HandleAsync(factory, Query());

        result.Value.Rows.Select(r => r.Operation).Should().Equal(
            "Newer", "Older");
    }

    [Fact]
    public async Task Filters_by_connection_command_operation_and_error_family()
    {
        var commandId = Guid.NewGuid();

        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await SeedAsync(factory,
            Row(Tenant, ConnectionA, "CreateCustomer", Now.AddHours(-1), commandId: commandId,
                family: ErrorFamily.Technical, code: IntegrationErrors.MappingMissing),
            Row(Tenant, ConnectionA, "CreateCustomer", Now.AddHours(-2)),
            Row(Tenant, ConnectionB, "GetBalance", Now.AddHours(-3)));

        (await HandleAsync(factory, Query() with { ConnectionId = ConnectionB }))
            .Value.Rows.Should().ContainSingle().Which.Operation.Should().Be("GetBalance");

        (await HandleAsync(factory, Query() with { CommandId = commandId }))
            .Value.Rows.Should().ContainSingle().Which.CommandId.Should().Be(commandId);

        (await HandleAsync(factory, Query() with { Operation = "  CreateCustomer  " }))
            .Value.Rows.Should().HaveCount(2, "the filter is trimmed, and matched exactly");

        (await HandleAsync(factory, Query() with { ErrorFamily = "technical" }))
            .Value.Rows.Should().ContainSingle().Which.ErrorFamily.Should().Be("Technical");
    }

    [Fact]
    public async Task An_unknown_error_family_is_refused_by_name_rather_than_ignored()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await SeedAsync(factory, Row(Tenant, ConnectionA, "CreateCustomer", Now.AddHours(-1)));

        var result = await HandleAsync(factory, Query() with { ErrorFamily = "Catastrophic" });

        result.IsFailure.Should().BeTrue(
            "ignoring the filter would answer with the unfiltered list, which reads as "
            + "'there were no technical failures'");
        result.Error.Should().Contain("INTEGRATION_ERROR_FAMILY_UNKNOWN");
        result.Error.Should().Contain("Transient", "the message names the accepted values");
    }

    [Fact]
    public async Task Pages_without_repeating_or_skipping_rows_recorded_in_the_same_instant()
    {
        var sameInstant = Now.AddHours(-1);

        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await SeedAsync(factory, [.. Enumerable.Range(0, 5)
            .Select(i => Row(Tenant, ConnectionA, $"Call{i}", sameInstant))]);

        var first = await HandleAsync(factory, Query() with { Page = 1, PageSize = 2 });
        var second = await HandleAsync(factory, Query() with { Page = 2, PageSize = 2 });
        var third = await HandleAsync(factory, Query() with { Page = 3, PageSize = 2 });

        first.Value.TotalCount.Should().Be(5);
        first.Value.TotalPages.Should().Be(3);

        var ids = first.Value.Rows.Concat(second.Value.Rows).Concat(third.Value.Rows)
            .Select(r => r.Id).ToList();

        ids.Should().HaveCount(5).And.OnlyHaveUniqueItems(
            "the Id tie-break is what keeps paging stable when At alone is not unique");
    }

    [Fact]
    public async Task A_page_below_one_is_clamped_rather_than_producing_a_negative_offset()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await SeedAsync(factory, Row(Tenant, ConnectionA, "CreateCustomer", Now.AddHours(-1)));

        var result = await HandleAsync(factory, Query() with { Page = 0, PageSize = -5 });

        result.IsSuccess.Should().BeTrue();
        result.Value.Page.Should().Be(1);
        result.Value.PageSize.Should().Be(20);
    }

    [Fact]
    public async Task Another_tenants_calls_are_invisible_and_its_connection_id_is_an_empty_page()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await SeedAsync(factory,
            Row(Tenant, ConnectionA, "Mine", Now.AddHours(-1)),
            Row(OtherTenant, ConnectionB, "Theirs", Now.AddHours(-1)));

        var all = await HandleAsync(factory, Query());
        all.Value.Rows.Should().ContainSingle().Which.Operation.Should().Be("Mine");

        var theirConnection = await HandleAsync(factory, Query() with { ConnectionId = ConnectionB });
        theirConnection.IsSuccess.Should().BeTrue(
            "an empty page and never a refusal — a 403 would confirm that connection exists");
        theirConnection.Value.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task An_inverted_window_is_read_the_way_it_was_obviously_meant()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await SeedAsync(factory, Row(Tenant, ConnectionA, "CreateCustomer", Now.AddDays(-2)));

        var result = await HandleAsync(factory, Query() with { From = Now, To = Now.AddDays(-5) });

        result.Value.From.Should().Be(Now.AddDays(-5));
        result.Value.To.Should().Be(Now);
        result.Value.Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Returns_every_journalled_column_including_the_correlation_id()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await SeedAsync(factory,
            Row(Tenant, ConnectionA, "CreateCustomer", Now.AddHours(-1),
                family: ErrorFamily.Transient, code: IntegrationErrors.Timeout));

        var row = (await HandleAsync(factory, Query())).Value.Rows.Should().ContainSingle().Which;

        row.Operation.Should().Be("CreateCustomer");
        row.Endpoint.Should().Be("/party/v2/customers");
        row.HttpStatus.Should().Be(504);
        row.DurationMs.Should().Be(1_200);
        row.ErrorFamily.Should().Be("Transient");
        row.ErrorCode.Should().Be(IntegrationErrors.Timeout);
        row.CorrelationId.Should().Be("corr-1");
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private static ListCallLogQuery Query() =>
        new(null, null, null, null, null, null, Page: 1, PageSize: 20);

    private static async Task<Result<CallLogPage>> HandleAsync(
        TestIntegrationDbContextFactory factory, ListCallLogQuery query)
    {
        await using var db = factory.CreateContext();
        var handler = new ListCallLogHandler(db, new FrozenClock(Now));
        return await handler.Handle(query, CancellationToken.None);
    }

    private static async Task SeedAsync(
        TestIntegrationDbContextFactory factory, params IntegrationCallLog[] rows)
    {
        // Inserted through a tenant-A context on purpose: EF query filters apply to reads, not to
        // inserts, so the other tenant's row really does land in the same store — which is what
        // makes the isolation assertion mean something.
        await using var db = factory.CreateContext();
        db.CallLogs.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static IntegrationCallLog Row(
        Guid tenantId,
        Guid connectionId,
        string operation,
        DateTimeOffset at,
        Guid? commandId = null,
        ErrorFamily? family = null,
        string? code = null,
        long durationMs = 1_200) =>
        IntegrationCallLog.Record(
            tenantId: tenantId,
            connectionId: connectionId,
            operation: operation,
            durationMs: durationMs,
            at: at,
            commandId: commandId,
            endpoint: "/party/v2/customers",
            httpStatus: 504,
            errorFamily: family,
            errorCode: code,
            correlationId: "corr-1");

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

namespace Sankore.Modules.Integration.Tests.Features.Balance;

using System.Net;
using FluentAssertions;
using Sankore.Modules.Integration.Features.Balance;
using Sankore.Modules.Integration.Infrastructure.Resilience;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.Features.Commands;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// INT-15 criteria 1 and 2 on <c>IIntegrationModule.CoreBanking.GetLiveBalanceAsync</c>: the live
/// call behind a 60-second cache per account, and the snapshot fallback — with its date and its
/// stale flag — whenever the breaker is open or the call fails.
///
/// <para>
/// Most of these assertions are about a call that did NOT happen, which is why the port is a
/// recording double and not a substitute: <c>BalanceCalls</c> being empty is the property, and a
/// cache that quietly stopped working would otherwise pass every one of them.
/// </para>
/// </summary>
public sealed class GetLiveBalanceFacadeTests
{
    private static TestIntegrationDbContextFactory NewFactory()
        => new(BalanceTestHarness.TenantId);

    [Fact]
    public async Task A_live_balance_is_returned_and_cached_for_sixty_seconds()
    {
        using var factory = NewFactory();
        await using var db = factory.CreateContext();

        var connection = BalanceTestHarness.Connection();
        db.Connections.Add(connection);
        db.References.Add(BalanceTestHarness.Reference(connection.Id));
        await db.SaveChangesAsync();

        var adapter = new BalanceTestHarness.RecordingAccountAdapter();
        var cache = new BalanceTestHarness.RecordingCache();
        var facade = BalanceTestHarness.Facade(db, adapter, BalanceTestHarness.Gate(cache));

        var balance = await facade.CoreBanking.GetLiveBalanceAsync(
            BalanceTestHarness.CrmCustomerId, BalanceTestHarness.AccountId, default);

        balance.Should().NotBeNull();
        balance!.Balance.Should().Be(125_000m);
        balance.Currency.Should().Be("XOF");
        balance.IsStale.Should().BeFalse("the CBS answered");

        adapter.BalanceCalls.Should().ContainSingle();
        cache.Writes.Should().ContainSingle();

        // Absolute and never sliding: a sliding window is refreshed by every reader, so a busy
        // account would keep serving a figure minutes old for as long as people kept asking.
        cache.LastWriteOptions!.AbsoluteExpirationRelativeToNow.Should().Be(TimeSpan.FromSeconds(60));
        cache.LastWriteOptions.SlidingExpiration.Should().BeNull();
    }

    [Fact]
    public async Task A_second_read_inside_the_window_does_not_reach_the_port()
    {
        using var factory = NewFactory();
        await using var db = factory.CreateContext();

        var connection = BalanceTestHarness.Connection();
        db.Connections.Add(connection);
        db.References.Add(BalanceTestHarness.Reference(connection.Id));
        await db.SaveChangesAsync();

        var adapter = new BalanceTestHarness.RecordingAccountAdapter();
        var facade = BalanceTestHarness.Facade(
            db, adapter, BalanceTestHarness.Gate(new BalanceTestHarness.RecordingCache()));

        var first = await facade.CoreBanking.GetLiveBalanceAsync(
            BalanceTestHarness.CrmCustomerId, BalanceTestHarness.AccountId, default);

        var second = await facade.CoreBanking.GetLiveBalanceAsync(
            BalanceTestHarness.CrmCustomerId, BalanceTestHarness.AccountId, default);

        // ONE call for two reads. The absence of the second call is the whole of criterion 1.
        adapter.BalanceCalls.Should().ContainSingle();

        second.Should().NotBeNull();
        second!.Balance.Should().Be(first!.Balance);
        second.AsOf.Should().Be(first.AsOf, "a cached figure keeps the moment it was true");
        second.IsStale.Should().BeFalse("a cache hit is a live figure a moment old, not a stale one");
    }

    [Fact]
    public async Task An_open_breaker_answers_from_the_snapshot_with_its_date_and_the_stale_flag()
    {
        using var factory = NewFactory();
        await using var db = factory.CreateContext();

        var connection = BalanceTestHarness.Connection(failureThreshold: 2);
        var snapshotAt = CommandsTestHarness.Now.AddHours(-9);

        db.Connections.Add(connection);
        db.References.Add(BalanceTestHarness.Reference(connection.Id));
        db.CbsSnapshots.Add(BalanceTestHarness.Snapshot(
            connection.Id, balance: 75_000m, snapshotAt: snapshotAt));
        await db.SaveChangesAsync();

        // The real INT-09 provider, opened the way production opens it: two consecutive 5xx on
        // this connection's pipeline. Nothing here reaches into the breaker's state.
        var pipelines = new IntegrationResiliencePipelineProvider();
        for (var i = 0; i < 2; i++)
        {
            await pipelines.GetWritePipeline(connection).ExecuteAsync(
                _ => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        }

        var adapter = new BalanceTestHarness.RecordingAccountAdapter();
        var cache = new BalanceTestHarness.RecordingCache();

        var facade = BalanceTestHarness.Facade(
            db, adapter, BalanceTestHarness.Gate(cache, pipelines));

        var balance = await facade.CoreBanking.GetLiveBalanceAsync(
            BalanceTestHarness.CrmCustomerId, BalanceTestHarness.AccountId, default);

        balance.Should().NotBeNull();
        balance!.IsStale.Should().BeTrue();
        balance.Balance.Should().Be(75_000m, "this is the snapshot's figure, not the port's");
        balance.AsOf.Should().Be(snapshotAt, "the moment the figure was true, not now");

        // Not even attempted: Polly's half-open trial call is scarce and the write path, which has
        // no fallback, needs it more than a screen refresh does.
        adapter.BalanceCalls.Should().BeEmpty();
        cache.Writes.Should().BeEmpty("a stale value is never cached");
    }

    [Fact]
    public async Task A_failing_call_answers_from_the_snapshot_with_the_stale_flag()
    {
        using var factory = NewFactory();
        await using var db = factory.CreateContext();

        var connection = BalanceTestHarness.Connection();
        var snapshotAt = CommandsTestHarness.Now.AddHours(-3);

        db.Connections.Add(connection);
        db.References.Add(BalanceTestHarness.Reference(connection.Id));
        db.CbsSnapshots.Add(BalanceTestHarness.Snapshot(
            connection.Id, balance: 42_000m, snapshotAt: snapshotAt));
        await db.SaveChangesAsync();

        var adapter = new BalanceTestHarness.RecordingAccountAdapter(
            _ => IntegrationResult.Transient<CbsBalance>(IntegrationErrors.Unavailable));

        var cache = new BalanceTestHarness.RecordingCache();

        var facade = BalanceTestHarness.Facade(db, adapter, BalanceTestHarness.Gate(cache));

        var balance = await facade.CoreBanking.GetLiveBalanceAsync(
            BalanceTestHarness.CrmCustomerId, BalanceTestHarness.AccountId, default);

        balance.Should().NotBeNull();
        balance!.IsStale.Should().BeTrue();
        balance.Balance.Should().Be(42_000m);
        balance.AsOf.Should().Be(snapshotAt);

        adapter.BalanceCalls.Should().ContainSingle("the CBS was asked, and could not answer");
    }

    [Fact]
    public async Task A_stale_value_is_not_written_to_the_cache()
    {
        using var factory = NewFactory();
        await using var db = factory.CreateContext();

        var connection = BalanceTestHarness.Connection();
        db.Connections.Add(connection);
        db.References.Add(BalanceTestHarness.Reference(connection.Id));
        db.CbsSnapshots.Add(BalanceTestHarness.Snapshot(connection.Id, balance: 42_000m));
        await db.SaveChangesAsync();

        var adapter = new BalanceTestHarness.RecordingAccountAdapter(
            _ => IntegrationResult.Transient<CbsBalance>(IntegrationErrors.Timeout));

        var cache = new BalanceTestHarness.RecordingCache();
        var facade = BalanceTestHarness.Facade(db, adapter, BalanceTestHarness.Gate(cache));

        var first = await facade.CoreBanking.GetLiveBalanceAsync(
            BalanceTestHarness.CrmCustomerId, BalanceTestHarness.AccountId, default);

        first!.IsStale.Should().BeTrue();
        cache.Writes.Should().BeEmpty();

        // And the consequence that matters: the CBS is asked AGAIN on the next read. Caching the
        // fallback would freeze the figure for a further 60 seconds after the CBS came back.
        await facade.CoreBanking.GetLiveBalanceAsync(
            BalanceTestHarness.CrmCustomerId, BalanceTestHarness.AccountId, default);

        adapter.BalanceCalls.Should().HaveCount(2);
    }

    [Fact]
    public async Task No_snapshot_and_a_failing_call_answers_null_rather_than_a_zero()
    {
        using var factory = NewFactory();
        await using var db = factory.CreateContext();

        var connection = BalanceTestHarness.Connection();
        db.Connections.Add(connection);

        // The reference makes the account ours; no snapshot row exists at all.
        db.References.Add(BalanceTestHarness.Reference(connection.Id));
        await db.SaveChangesAsync();

        var adapter = new BalanceTestHarness.RecordingAccountAdapter(
            _ => IntegrationResult.Transient<CbsBalance>(IntegrationErrors.Unavailable));

        var facade = BalanceTestHarness.Facade(
            db, adapter, BalanceTestHarness.Gate(new BalanceTestHarness.RecordingCache()));

        var balance = await facade.CoreBanking.GetLiveBalanceAsync(
            BalanceTestHarness.CrmCustomerId, BalanceTestHarness.AccountId, default);

        // A zero balance at a counter is indistinguishable from an empty account, and would be
        // quoted to the customer as one. Null says "we do not know", which the screen can render.
        balance.Should().BeNull();
    }

    [Fact]
    public async Task A_container_without_a_distributed_cache_still_answers()
    {
        using var factory = NewFactory();
        await using var db = factory.CreateContext();

        var connection = BalanceTestHarness.Connection();
        db.Connections.Add(connection);
        db.References.Add(BalanceTestHarness.Reference(connection.Id));
        await db.SaveChangesAsync();

        var adapter = new BalanceTestHarness.RecordingAccountAdapter();

        // A gate holding no IDistributedCache at all — the deployment with no Redis.
        var facade = BalanceTestHarness.Facade(db, adapter, BalanceTestHarness.Gate(cache: null));

        var first = await facade.CoreBanking.GetLiveBalanceAsync(
            BalanceTestHarness.CrmCustomerId, BalanceTestHarness.AccountId, default);

        var second = await facade.CoreBanking.GetLiveBalanceAsync(
            BalanceTestHarness.CrmCustomerId, BalanceTestHarness.AccountId, default);

        first!.Balance.Should().Be(125_000m);
        second!.Balance.Should().Be(125_000m);

        // Uncached, so every read is a real call. Degraded, never broken.
        adapter.BalanceCalls.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_container_without_the_balance_gate_still_answers()
    {
        using var factory = NewFactory();
        await using var db = factory.CreateContext();

        var connection = BalanceTestHarness.Connection();
        db.Connections.Add(connection);
        db.References.Add(BalanceTestHarness.Reference(connection.Id));
        await db.SaveChangesAsync();

        var adapter = new BalanceTestHarness.RecordingAccountAdapter();

        // No gate at all: a host that has not wired AddBalanceServices. The write paths of this
        // facade do not use it, so its absence must not fail them either.
        var facade = BalanceTestHarness.Facade(db, adapter, gate: null);

        var balance = await facade.CoreBanking.GetLiveBalanceAsync(
            BalanceTestHarness.CrmCustomerId, BalanceTestHarness.AccountId, default);

        balance!.Balance.Should().Be(125_000m);
        balance.IsStale.Should().BeFalse();
    }

    [Fact]
    public async Task A_broken_cache_does_not_fail_the_read()
    {
        using var factory = NewFactory();
        await using var db = factory.CreateContext();

        var connection = BalanceTestHarness.Connection();
        db.Connections.Add(connection);
        db.References.Add(BalanceTestHarness.Reference(connection.Id));
        await db.SaveChangesAsync();

        var adapter = new BalanceTestHarness.RecordingAccountAdapter();
        var cache = new BalanceTestHarness.RecordingCache(throwOnEveryCall: true);

        var facade = BalanceTestHarness.Facade(db, adapter, BalanceTestHarness.Gate(cache));

        var balance = await facade.CoreBanking.GetLiveBalanceAsync(
            BalanceTestHarness.CrmCustomerId, BalanceTestHarness.AccountId, default);

        // A cache is an optimisation, never a dependency: a Redis node being restarted must not
        // stop a counter from reading a balance.
        balance.Should().NotBeNull();
        balance!.Balance.Should().Be(125_000m);
        balance.IsStale.Should().BeFalse();

        cache.Reads.Should().ContainSingle("the read was attempted, and its failure swallowed");
        adapter.BalanceCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task The_cache_key_carries_the_tenant_the_connection_and_the_account()
    {
        using var factory = NewFactory();
        await using var db = factory.CreateContext();

        var connection = BalanceTestHarness.Connection();
        db.Connections.Add(connection);
        db.References.Add(BalanceTestHarness.Reference(connection.Id));
        await db.SaveChangesAsync();

        var cache = new BalanceTestHarness.RecordingCache();

        var facade = BalanceTestHarness.Facade(
            db, new BalanceTestHarness.RecordingAccountAdapter(), BalanceTestHarness.Gate(cache));

        await facade.CoreBanking.GetLiveBalanceAsync(
            BalanceTestHarness.CrmCustomerId, BalanceTestHarness.AccountId, default);

        var expected = LiveBalanceGate.CacheKey(
            BalanceTestHarness.TenantId, connection.Id, BalanceTestHarness.AccountId);

        cache.Writes.Should().ContainSingle().Which.Should().Be(expected);

        // Redis is one shared instance across tenants, so a key without the tenant is a
        // cross-tenant read wherever two IMFs run sequential CBS account numbering — which is the
        // norm, not an edge case.
        expected.Should().Be(
            $"integration:balance:{BalanceTestHarness.TenantId}:{connection.Id}:{BalanceTestHarness.AccountId}");

        expected.Should().Contain(BalanceTestHarness.TenantId.ToString());
    }

    [Fact]
    public async Task Two_accounts_of_one_customer_do_not_share_an_entry()
    {
        using var factory = NewFactory();
        await using var db = factory.CreateContext();

        var connection = BalanceTestHarness.Connection();
        const string second = "CBS-ACC-0002";

        db.Connections.Add(connection);
        db.References.Add(BalanceTestHarness.Reference(connection.Id));
        db.References.Add(BalanceTestHarness.Reference(connection.Id, accountId: second));
        await db.SaveChangesAsync();

        var adapter = new BalanceTestHarness.RecordingAccountAdapter(
            id => IntegrationResult.Ok(
                BalanceTestHarness.LiveBalance(
                    balance: id.Value == second ? 1_000m : 125_000m, accountId: id.Value)));

        var cache = new BalanceTestHarness.RecordingCache();
        var facade = BalanceTestHarness.Facade(db, adapter, BalanceTestHarness.Gate(cache));

        var first = await facade.CoreBanking.GetLiveBalanceAsync(
            BalanceTestHarness.CrmCustomerId, BalanceTestHarness.AccountId, default);

        var other = await facade.CoreBanking.GetLiveBalanceAsync(
            BalanceTestHarness.CrmCustomerId, second, default);

        // Keyed per account: the second read must not be served the first account's figure.
        first!.Balance.Should().Be(125_000m);
        other!.Balance.Should().Be(1_000m);

        adapter.BalanceCalls.Should().HaveCount(2);
        cache.Writes.Should().HaveCount(2);
        cache.Writes.Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task An_account_of_another_customer_answers_null_and_makes_no_call()
    {
        using var factory = NewFactory();
        await using var db = factory.CreateContext();

        var connection = BalanceTestHarness.Connection();
        var otherCustomer = new Guid("cccccccc-0000-0000-0000-00000000000f");

        db.Connections.Add(connection);

        // The account belongs to ANOTHER customer of the same tenant, and the snapshot of our
        // customer does not list it.
        db.References.Add(BalanceTestHarness.Reference(
            connection.Id, crmCustomerId: otherCustomer, accountId: "CBS-ACC-9999"));
        db.CbsSnapshots.Add(BalanceTestHarness.Snapshot(connection.Id));
        await db.SaveChangesAsync();

        var adapter = new BalanceTestHarness.RecordingAccountAdapter();
        var cache = new BalanceTestHarness.RecordingCache();
        var facade = BalanceTestHarness.Facade(db, adapter, BalanceTestHarness.Gate(cache));

        var balance = await facade.CoreBanking.GetLiveBalanceAsync(
            BalanceTestHarness.CrmCustomerId, "CBS-ACC-9999", default);

        balance.Should().BeNull();

        // §5bis(c): the absence of the call IS the control. And the cache is never even consulted,
        // so a hit cannot become a way around the ownership check.
        adapter.BalanceCalls.Should().BeEmpty();
        cache.Reads.Should().BeEmpty();
        cache.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task The_snapshot_account_number_is_accepted_and_the_call_uses_our_own_id()
    {
        using var factory = NewFactory();
        await using var db = factory.CreateContext();

        var connection = BalanceTestHarness.Connection();

        db.Connections.Add(connection);

        // No INT-07 reference: a portfolio SANKORE did not open, known only through the snapshot.
        db.CbsSnapshots.Add(BalanceTestHarness.Snapshot(connection.Id));
        await db.SaveChangesAsync();

        var adapter = new BalanceTestHarness.RecordingAccountAdapter();
        var cache = new BalanceTestHarness.RecordingCache();
        var facade = BalanceTestHarness.Facade(db, adapter, BalanceTestHarness.Gate(cache));

        var balance = await facade.CoreBanking.GetLiveBalanceAsync(
            BalanceTestHarness.CrmCustomerId, BalanceTestHarness.AccountNumber, default);

        balance.Should().NotBeNull();

        // Asked with the id from OUR records, never with the caller's string — and cached under
        // that same id, so the number and the id share one entry instead of two.
        adapter.BalanceCalls.Should().ContainSingle().Which.Should().Be(BalanceTestHarness.AccountId);

        cache.Writes.Should().ContainSingle().Which.Should().Be(
            LiveBalanceGate.CacheKey(
                BalanceTestHarness.TenantId, connection.Id, BalanceTestHarness.AccountId));
    }

    [Fact]
    public async Task A_batch_only_installation_answers_from_the_snapshot_without_calling()
    {
        using var factory = NewFactory();
        await using var db = factory.CreateContext();

        var connection = BalanceTestHarness.Connection();

        db.Connections.Add(connection);
        db.References.Add(BalanceTestHarness.Reference(connection.Id));
        db.CbsSnapshots.Add(BalanceTestHarness.Snapshot(connection.Id, balance: 75_000m));
        await db.SaveChangesAsync();

        var adapter = new BalanceTestHarness.RecordingAccountAdapter(
            balanceMode: CapabilityMode.Batch);

        var cache = new BalanceTestHarness.RecordingCache();
        var facade = BalanceTestHarness.Facade(db, adapter, BalanceTestHarness.Gate(cache));

        var balance = await facade.CoreBanking.GetLiveBalanceAsync(
            BalanceTestHarness.CrmCustomerId, BalanceTestHarness.AccountId, default);

        // There is no live call to make, so the figure can only be the last file that was loaded —
        // and it says so.
        balance!.IsStale.Should().BeTrue();
        adapter.BalanceCalls.Should().BeEmpty();
        cache.Writes.Should().BeEmpty();
    }
}

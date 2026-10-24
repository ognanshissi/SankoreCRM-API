namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance.PolicySync;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;

/// <summary>
/// ASS-07: the insurers' own view of their policies, into the read model BNF-B-17's reconciliation
/// and ASS-10's statement both read.
///
/// <para>
/// The case that drove the design is <see cref="An_unchanged_policy_announces_nothing"/>. A
/// nightly sweep over a portfolio that published an event per policy would have M08 notify every
/// customer, every night, about nothing — which is why <c>PolicyRecord.ApplySync</c> returns
/// whether the status MOVED and this job publishes only on that.
/// </para>
/// </summary>
public sealed class SyncPoliciesJobTests : IDisposable
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 3, 30, 0, TimeSpan.Zero);

    private const string ExternalCustomer = FakeAdapter.SeededCustomerId;
    private const string ExternalPolicy = FakeAdapter.SeededPolicyId;

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);
    private readonly FakeAdapter _adapter = new();
    private readonly List<PolicyStatusChangedEvent> _published = [];
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();

    public SyncPoliciesJobTests()
        => _publisher
            .When(p => p.PublishAsync(
                Arg.Any<PolicyStatusChangedEvent>(), Arg.Any<CancellationToken>()))
            .Do(call => _published.Add(call.Arg<PolicyStatusChangedEvent>()));

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private Guid SeedConnectionWithCustomer(bool withReference = true)
    {
        using var db = _factory.CreateContext();

        var connection = InsuranceTestHarness.SeedConnection(
            db, Tenant, IntegrationKind.Fake, name: "ORASS Vie");

        if (withReference)
        {
            db.References.Add(IntegrationReference.Create(
                tenantId: Tenant,
                connectionId: connection.Id,
                kind: IntegrationKind.Fake,
                entityType: IntegrationEntityTypes.Customer,
                crmId: Customer,
                externalId: ExternalCustomer,
                clock: InsuranceTestHarness.Clock()));
        }

        db.SaveChanges();
        return connection.Id;
    }

    private async Task RunAsync()
    {
        var services = new ServiceCollection();

        services.AddSingleton<TimeProvider>(new FixedSyncClock(Now));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddKeyedSingleton(nameof(IntegrationDbContext), _publisher);
        services.AddScoped(_ => _factory.CreateContext());
        services.AddScoped<IntegrationAdapterResolver>();
        // Keyed by the connection's kind, which is how IntegrationAdapterResolver finds it.
        services.AddKeyedSingleton<IInsurancePolicyPort>(IntegrationKind.Fake.ToString(), _adapter);
        services.AddKeyedSingleton<ICbsAdapter>(IntegrationKind.Fake.ToString(), _adapter);

        await using var provider = services.BuildServiceProvider();

        await new SyncPoliciesJob(
                new FixedSyncScopeFactory(provider), NullLogger<SyncPoliciesJob>.Instance)
            .RunAsync(Tenant, CancellationToken.None);
    }

    private async Task<List<PolicyRecord>> PoliciesAsync()
    {
        await using var db = _factory.CreateContext();
        return await db.Policies.IgnoreQueryFilters().ToListAsync();
    }

    private void SetInsurerStatus(PolicyStatus status, DateOnly? nextDue = null)
    {
        var existing = _adapter.PoliciesByCustomer[ExternalCustomer][0];

        _adapter.PoliciesByCustomer[ExternalCustomer][0] = existing with
        {
            Status = status,
            NextDueDate = nextDue ?? existing.NextDueDate,
        };
    }

    // ── first sight ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_policy_the_platform_has_never_seen_is_recorded()
    {
        // The reason this job exists: a policy sold at the insurer's own counter carries no saga,
        // so it exists for this platform only once a sweep has seen it.
        SeedConnectionWithCustomer();

        await RunAsync();

        var policy = (await PoliciesAsync()).Should().ContainSingle().Subject;
        policy.ExternalPolicyId.Should().Be(ExternalPolicy);
        policy.CrmCustomerId.Should().Be(Customer);
        policy.Status.Should().Be(PolicyStatus.Issued);
        policy.PremiumAmount.Should().Be(7_500m);
        policy.LastSyncedAt.Should().Be(Now);
    }

    [Fact]
    public async Task Discovering_a_policy_announces_NOTHING()
    {
        // Discovering a contract that has been running for a year is not a status change.
        // Announcing one would tell the customer their policy just became active.
        SeedConnectionWithCustomer();

        await RunAsync();

        _published.Should().BeEmpty();
    }

    // ── the second night ────────────────────────────────────────────────────

    [Fact]
    public async Task An_unchanged_policy_announces_nothing()
    {
        // The case the whole design turns on.
        SeedConnectionWithCustomer();
        await RunAsync();
        _published.Clear();

        await RunAsync();

        _published.Should().BeEmpty();
        (await PoliciesAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_status_that_MOVED_is_announced_once()
    {
        SeedConnectionWithCustomer();
        await RunAsync();
        _published.Clear();

        SetInsurerStatus(PolicyStatus.Suspended);
        await RunAsync();

        var evt = _published.Should().ContainSingle().Subject;

        evt.ExternalPolicyId.Should().Be(ExternalPolicy);
        evt.PreviousStatus.Should().Be(nameof(PolicyStatus.Issued));
        evt.CurrentStatus.Should().Be(nameof(PolicyStatus.Suspended));
        evt.CrmCustomerId.Should().Be(Customer);

        // And a third night with the same status says nothing further.
        _published.Clear();
        await RunAsync();
        _published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_change_that_is_NOT_the_status_updates_the_row_without_announcing()
    {
        // A new due date is worth storing — the statement reads it — and is not worth a
        // notification.
        SeedConnectionWithCustomer();
        await RunAsync();
        _published.Clear();

        SetInsurerStatus(PolicyStatus.Issued, nextDue: new DateOnly(2026, 9, 1));
        await RunAsync();

        _published.Should().BeEmpty();
        (await PoliciesAsync()).Should().ContainSingle()
            .Which.NextDueDate.Should().Be(new DateOnly(2026, 9, 1));
    }

    // ── what the sweep skips ────────────────────────────────────────────────

    [Fact]
    public async Task A_customer_with_no_reference_at_this_insurer_is_not_asked_about()
    {
        // The reference table is the only route that exists, exactly as the reconciliation run
        // enumerates its own side. A customer unknown to this insurer has no policies there.
        SeedConnectionWithCustomer(withReference: false);

        await RunAsync();

        (await PoliciesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_insurer_that_cannot_be_read_is_skipped_without_failing_the_run()
    {
        // A bordereau-fed insurer declares no read capability: its policies arrive by file, and
        // asking it for them would be asking the wrong question.
        SeedConnectionWithCustomer();
        _adapter.ForcedReadOutcome = IntegrationResult.Technical(
            IntegrationErrors.Unavailable, "l'assureur est injoignable");

        var act = async () => await RunAsync();

        await act.Should().NotThrowAsync();
        (await PoliciesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_inactive_connection_is_not_swept()
    {
        using (var db = _factory.CreateContext())
        {
            var connection = InsuranceTestHarness.SeedConnection(
                db, Tenant, IntegrationKind.Fake, name: "ORASS Vie", active: false);

            db.References.Add(IntegrationReference.Create(
                Tenant, connection.Id, IntegrationKind.Fake,
                IntegrationEntityTypes.Customer, Customer, ExternalCustomer,
                InsuranceTestHarness.Clock()));

            db.SaveChanges();
        }

        await RunAsync();

        (await PoliciesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_tenant_with_no_insurance_connection_does_nothing()
    {
        var act = async () => await RunAsync();

        await act.Should().NotThrowAsync();
        (await PoliciesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_SYSTEM_placeholder_tenant_is_skipped()
    {
        SeedConnectionWithCustomer();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FixedSyncClock(Now));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddKeyedSingleton(nameof(IntegrationDbContext), _publisher);
        services.AddScoped(_ => _factory.CreateContext());
        services.AddScoped<IntegrationAdapterResolver>();
        // Keyed by the connection's kind, which is how IntegrationAdapterResolver finds it.
        services.AddKeyedSingleton<IInsurancePolicyPort>(IntegrationKind.Fake.ToString(), _adapter);
        services.AddKeyedSingleton<ICbsAdapter>(IntegrationKind.Fake.ToString(), _adapter);

        await using var provider = services.BuildServiceProvider();

        await new SyncPoliciesJob(
                new FixedSyncScopeFactory(provider), NullLogger<SyncPoliciesJob>.Instance)
            .RunAsync(Guid.Empty, CancellationToken.None);

        (await PoliciesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Another_tenants_policy_is_not_touched()
    {
        SeedConnectionWithCustomer();
        await RunAsync();

        await using var other = _factory.ContextFor(Guid.NewGuid());
        var theirs = await other.Policies.ToListAsync();

        theirs.Should().BeEmpty("the query filter scopes the read model per tenant");
    }

    private sealed class FixedSyncClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedSyncScopeFactory(IServiceProvider sp) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(sp);

        private sealed class Scope(IServiceProvider sp) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = sp;
            public void Dispose() { }
        }
    }
}

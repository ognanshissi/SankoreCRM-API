namespace Sankore.Modules.Integration.Tests.Features.Sync;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Snapshot;
using Sankore.Modules.Integration.Features.Sync;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// The scaffolding the INT-20 suites share: an InMemory context, two clocks, seeders for the two
/// rows the sync actually depends on, and a projector double that records what it was handed.
///
/// <para>
/// Shaped on <c>DispatchTestContext</c>, deliberately: the two slices have the same job pattern,
/// and a reader who knows one should recognise the other. It is a separate file rather than a
/// reuse of that one because the two folders belong to different chantiers.
/// </para>
/// </summary>
internal static class SyncTestContext
{
    internal static readonly DateTimeOffset Now = new(2026, 4, 2, 8, 0, 0, TimeSpan.Zero);

    internal static readonly Guid Actor = Guid.Parse("55555555-5555-5555-5555-555555555555");

    internal static IntegrationDbContext NewDb(Guid tenantId, string? name = null)
        => new(
            new DbContextOptionsBuilder<IntegrationDbContext>()
                .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
                .Options,
            new FixedTenantContext(tenantId));

    /// <summary>
    /// A service provider shaped like the API's, for the orchestrator, which creates its own scope.
    /// <c>ITenantContext</c> is registered exactly as <c>Program.cs</c> registers it — reading the
    /// ambient background context first — because that registration is the whole reason
    /// <c>BackgroundJobContext.SetScope</c> has to happen before <c>CreateScope</c>.
    /// </summary>
    internal static ServiceProvider NewProvider(string databaseName)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedClock(Now));

        services.AddScoped<ITenantContext>(_ =>
            BackgroundJobContext.CurrentTenant
            ?? throw new InvalidOperationException(
                "No ambient tenant: the job created its scope before establishing one."));

        services.AddDbContext<IntegrationDbContext>(
            opt => opt.UseInMemoryDatabase(databaseName), ServiceLifetime.Scoped);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Seeds one connection. Written through the context it is given, so a test can seed a FOREIGN
    /// tenant's row through its own context — EF's query filters apply to reads, not to inserts.
    /// </summary>
    internal static IntegrationConnection SeedConnection(
        IntegrationDbContext db,
        Guid tenantId,
        IntegrationFamily family = IntegrationFamily.CoreBanking,
        bool active = true,
        ConnectionSettings? settings = null,
        string name = "CBS principal")
    {
        var clock = new FixedClock(Now);

        var connection = IntegrationConnection.Create(
            tenantId: tenantId,
            family: family,
            // Temenos because its settings record is the one with a real BaseUrl, and the sync
            // never resolves an adapter: which kind it is has no bearing on these tests.
            kind: IntegrationKind.Temenos,
            mode: IntegrationMode.Api,
            name: name,
            settings: settings ?? Settings(),
            createdBy: Actor,
            clock: clock);

        if (active)
        {
            connection.RecordHealth(IntegrationHealth.Healthy(TimeSpan.FromMilliseconds(40), Now), clock);
            connection.Activate(Actor, clock);
        }

        db.Connections.Add(connection);
        db.SaveChanges();

        return connection;
    }

    /// <summary>Temenos settings, optionally carrying per-stream sync periods.</summary>
    internal static TemenosSettings Settings(IReadOnlyDictionary<SyncStream, int>? intervals = null)
        => new()
        {
            BaseUrl = "https://cbs.example.ci/api/",
            AuthMode = TemenosAuthMode.StaticToken,
            CompanyId = "CI0010001",
            SyncIntervalMinutes = intervals,
        };

    /// <summary>
    /// Seeds one <c>integration_reference</c> row — the table that decides, on its own, which
    /// customers a sweep touches (criterion 3).
    /// </summary>
    internal static IntegrationReference SeedReference(
        IntegrationDbContext db,
        Guid tenantId,
        Guid connectionId,
        Guid crmId,
        string externalId,
        string entityType = IntegrationEntityTypes.Customer,
        DateTimeOffset? createdAt = null)
    {
        var reference = IntegrationReference.Create(
            tenantId: tenantId,
            connectionId: connectionId,
            kind: IntegrationKind.Temenos,
            entityType: entityType,
            crmId: crmId,
            externalId: externalId,
            clock: new FixedClock(createdAt ?? Now));

        db.References.Add(reference);
        db.SaveChanges();

        return reference;
    }

    internal static IntegrationSyncCursor SeedCursor(
        IntegrationDbContext db,
        Guid tenantId,
        Guid connectionId,
        SyncStream stream,
        DateTimeOffset? lastRunAt = null,
        string? cursor = null)
    {
        var row = IntegrationSyncCursor.Create(tenantId, connectionId, stream);

        if (cursor is not null) row.Advance(cursor, new FixedClock(lastRunAt ?? Now));
        if (lastRunAt is not null) row.BeginRun(new FixedClock(lastRunAt.Value));

        db.SyncCursors.Add(row);
        db.SaveChanges();

        return row;
    }

    internal static IntegrationSyncCursor? ReadCursor(
        IntegrationDbContext db, Guid tenantId, Guid connectionId, SyncStream stream)
        => db.SyncCursors
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefault(
                c => c.TenantId == tenantId && c.ConnectionId == connectionId && c.Stream == stream);

    internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// A clock that moves one minute every time it is read. It is what makes "the cursor is
    /// advanced to the instant the run STARTED" an assertable claim: under a frozen clock the
    /// start and the end of a run are the same value and the test would pass either way.
    /// </summary>
    internal sealed class SteppingClock : TimeProvider
    {
        private int _reads;

        internal SteppingClock(DateTimeOffset start) => First = start;

        /// <summary>The first instant this clock ever handed out — the run's start.</summary>
        internal DateTimeOffset First { get; }

        public override DateTimeOffset GetUtcNow() => First.AddMinutes(_reads++);
    }

    /// <summary>
    /// The INT-21 seam's double. Hand-written rather than an NSubstitute mock because two of these
    /// tests need it to run code mid-call — one to throw on a chosen customer, one to observe the
    /// committed cursor from another connection while the sweep is still in flight.
    /// </summary>
    internal sealed class RecordingProjector : ICbsSnapshotProjector
    {
        private readonly Func<Guid, Task>? _onProject;
        private readonly Guid? _throwOn;

        internal RecordingProjector(Guid? throwOnCrmId = null, Func<Guid, Task>? onProject = null)
        {
            _throwOn = throwOnCrmId;
            _onProject = onProject;
        }

        internal List<(Guid TenantId, Guid ConnectionId, Guid CrmCustomerId)> Projected { get; } = [];

        public async Task ProjectAsync(
            Guid tenantId, Guid connectionId, Guid crmCustomerId, CancellationToken ct)
        {
            if (_onProject is not null) await _onProject(crmCustomerId);

            if (_throwOn == crmCustomerId)
                throw new InvalidOperationException("The external system refused the read.");

            Projected.Add((tenantId, connectionId, crmCustomerId));
        }
    }
}

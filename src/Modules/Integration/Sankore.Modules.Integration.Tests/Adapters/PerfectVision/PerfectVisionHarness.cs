namespace Sankore.Modules.Integration.Tests.Adapters.PerfectVision;

using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Adapters.PerfectVision;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;

/// <summary>
/// One Perfect Vision connection in a store, and the adapter bound to its tenant.
///
/// <para>
/// The adapter reads its installation's settings from the connection row — that is how INT-28's
/// criterion 3 makes the matrix depend on the installation — so a test of the matrix has to go
/// through a row and not through a constructor argument. This harness is that row, in one place,
/// so the tests assert on outcomes rather than on EF plumbing.
/// </para>
///
/// <para>
/// Everything is fixed: one tenant id, one actor, and the system clock used only for
/// <c>CreatedAt</c>, which nothing here asserts on. A <c>Guid.NewGuid()</c> in a fixture is the
/// one thing that varies between two runs.
/// </para>
/// </summary>
internal sealed class PerfectVisionHarness : IDisposable
{
    public static readonly Guid TenantId = new("44444444-4444-4444-4444-444444444444");

    private static readonly Guid Actor = new("55555555-5555-5555-5555-555555555555");

    private readonly TestIntegrationDbContextFactory _factory;
    private readonly IntegrationDbContext _db;

    private PerfectVisionHarness(
        TestIntegrationDbContextFactory factory, IntegrationDbContext db, PerfectVisionAdapter adapter)
    {
        _factory = factory;
        _db = db;
        Adapter = adapter;
    }

    public PerfectVisionAdapter Adapter { get; }

    /// <summary>
    /// A tenant whose Perfect Vision connection carries <paramref name="settings"/>.
    /// </summary>
    /// <param name="mode">
    /// <see cref="IntegrationMode.Batch"/> by default, which is what Perfect Vision is;
    /// <see cref="IntegrationMode.Relay"/> is the same installation reached through the
    /// on-premise agent (INT-26) and must change nothing about the matrix.
    /// </param>
    public static PerfectVisionHarness With(
        PerfectVisionSettings settings, IntegrationMode mode = IntegrationMode.Batch)
        => Build(settings, mode);

    /// <summary>
    /// A tenant with no Perfect Vision connection at all — the case the matrix must answer
    /// narrowly instead of throwing, since a screen asks for it before anything is configured.
    /// </summary>
    public static PerfectVisionHarness WithNoConnection() => Build(null, IntegrationMode.Batch);

    private static PerfectVisionHarness Build(PerfectVisionSettings? settings, IntegrationMode mode)
    {
        var factory = new TestIntegrationDbContextFactory(TenantId);

        if (settings is not null)
        {
            using var seed = factory.CreateContext();

            seed.Connections.Add(ConnectionCarrying(settings, mode));
            seed.SaveChanges();
        }

        // A second context over the same store, the way a request gets its own: the adapter has to
        // read the row back rather than see the instance the seeding context was tracking.
        var db = factory.CreateContext();

        var adapter = new PerfectVisionAdapter(
            db,
            new FixedTenantContext(TenantId),
            TimeProvider.System,
            NullLogger<PerfectVisionAdapter>.Instance);

        return new PerfectVisionHarness(factory, db, adapter);
    }

    /// <summary>
    /// A connection row carrying <paramref name="settings"/>, for <c>CheckHealthAsync</c> — which
    /// takes one explicitly because it is called before any connection is active.
    ///
    /// <para>
    /// The row's kind follows the settings' own <c>ExpectedKind</c>: the aggregate refuses a
    /// mismatch, so a test that wants a Perfect Vision connection carrying another kind's settings
    /// cannot build one this way, which is exactly the guarantee the aggregate advertises.
    /// </para>
    /// </summary>
    public static IntegrationConnection ConnectionCarrying(
        ConnectionSettings settings, IntegrationMode mode = IntegrationMode.Batch)
        => IntegrationConnection.Create(
            TenantId,
            IntegrationFamily.CoreBanking,
            settings.ExpectedKind,
            mode,
            "Perfect Vision — agence centrale",
            settings,
            Actor,
            TimeProvider.System);

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }
}

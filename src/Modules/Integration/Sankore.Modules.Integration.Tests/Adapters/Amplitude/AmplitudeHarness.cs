namespace Sankore.Modules.Integration.Tests.Adapters.Amplitude;

using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Adapters.Amplitude;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;

/// <summary>
/// One Amplitude connection in a store, and the adapter bound to its tenant.
///
/// <para>
/// The adapter reads its installation's release AND its connection mode from the row — that is how
/// INT-31's criterion 3 makes the matrix depend on the installation — so a test of the matrix has
/// to go through a row and not through a constructor argument. This harness is that row, in one
/// place, so the tests assert on outcomes rather than on EF plumbing.
/// </para>
///
/// <para>
/// Everything is fixed: one tenant id, one actor, and the system clock used only for
/// <c>CreatedAt</c>, which nothing here asserts on. A <c>Guid.NewGuid()</c> in a fixture is the one
/// thing that varies between two runs.
/// </para>
/// </summary>
internal sealed class AmplitudeHarness : IDisposable
{
    public static readonly Guid TenantId = new("66666666-6666-6666-6666-666666666666");

    private static readonly Guid Actor = new("77777777-7777-7777-7777-777777777777");

    private readonly TestIntegrationDbContextFactory _factory;
    private readonly IntegrationDbContext _db;

    private AmplitudeHarness(
        TestIntegrationDbContextFactory factory, IntegrationDbContext db, AmplitudeAdapter adapter)
    {
        _factory = factory;
        _db = db;
        Adapter = adapter;
    }

    public AmplitudeAdapter Adapter { get; }

    /// <summary>
    /// A tenant whose Amplitude connection runs <paramref name="version"/> and is reached in
    /// <paramref name="mode"/>.
    /// </summary>
    /// <param name="version">
    /// The installed release. The default is <see cref="AmplitudeVersion.Legacy"/> because that is
    /// the settings record's own default — a row written before the field meant anything reads as
    /// pre-Up, which is the narrow direction.
    /// </param>
    /// <param name="mode">
    /// <see cref="IntegrationMode.Batch"/> by default, which is the only carrier a pre-Up release
    /// has and a legitimate choice for an Up one.
    /// </param>
    public static AmplitudeHarness With(
        AmplitudeVersion version = AmplitudeVersion.Legacy,
        IntegrationMode mode = IntegrationMode.Batch)
        => Build(Settings(version), mode);

    /// <summary>
    /// A tenant with no Amplitude connection at all — the case the matrix must answer narrowly
    /// instead of throwing, since a screen asks for it before anything is configured.
    /// </summary>
    public static AmplitudeHarness WithNoConnection() => Build(null, IntegrationMode.Batch);

    /// <summary>
    /// Settings for one release. Carries the SFTP coordinates and a base URL that are required of
    /// a real connection but that this adapter never reads: they are here so the fixture resembles
    /// a configured installation rather than a minimal one, and <c>example.invalid</c> is a name
    /// the DNS cannot resolve, so a test that somehow made a call would fail loudly.
    /// </summary>
    public static AmplitudeSettings Settings(AmplitudeVersion version)
        => new()
        {
            AmplitudeVersion = version,
            BaseUrl = version == AmplitudeVersion.Up ? "https://amplitude.example.invalid/" : null,
            SftpHost = "sftp.example.invalid",
            OutboundDirectory = "/out",
            InboundDirectory = "/in",
        };

    private static AmplitudeHarness Build(AmplitudeSettings? settings, IntegrationMode mode)
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

        var adapter = new AmplitudeAdapter(
            db,
            new FixedTenantContext(TenantId),
            TimeProvider.System,
            NullLogger<AmplitudeAdapter>.Instance);

        return new AmplitudeHarness(factory, db, adapter);
    }

    /// <summary>
    /// A connection row carrying <paramref name="settings"/>, for <c>CheckHealthAsync</c> — which
    /// takes one explicitly because it is called before any connection is active.
    ///
    /// <para>
    /// The row's kind follows the settings' own <c>ExpectedKind</c>: the aggregate refuses a
    /// mismatch, so a test that wants an Amplitude connection carrying another kind's settings
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
            "Amplitude — siège",
            settings,
            Actor,
            TimeProvider.System);

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }
}

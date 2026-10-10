namespace Sankore.Modules.Integration.Tests.Adapters.Sab;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Adapters.Sab;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;

/// <summary>
/// One SAB connection in a store, a vault that either holds its API key or does not, and the
/// adapter bound to that tenant.
///
/// <para>
/// The adapter reads its installation from the connection row — that is how the entity of
/// criterion 2 reaches the call path and the capability matrix — so a test of either has to go
/// through a row and not through a constructor argument. This harness is that row, in one place,
/// so the tests assert on outcomes rather than on EF plumbing.
/// </para>
///
/// <para>
/// Everything is fixed, including the connection id: it is half of the vault key the API key is
/// stored under, so a test can assert on the exact <see cref="SecretKey"/> the adapter asked for.
/// A <c>Guid.NewGuid()</c> in a fixture is the one thing that varies between two runs.
/// </para>
/// </summary>
internal sealed class SabHarness : IDisposable
{
    public static readonly Guid TenantId = new("66666666-6666-6666-6666-666666666666");

    public static readonly Guid ConnectionId = new("77777777-7777-7777-7777-777777777777");

    /// <summary>
    /// An obvious placeholder. <b>No plausible Open SAB entity code appears anywhere in this
    /// chantier</b>: the condition under test is "is an entity configured", and a realistic-looking
    /// code is the first invented vendor identifier in the repository — the thing somebody later
    /// mistakes for documentation.
    /// </summary>
    public const string PlaceholderEntity = "PLACEHOLDER_ENTITY";

    private static readonly Guid Actor = new("88888888-8888-8888-8888-888888888888");

    private readonly TestIntegrationDbContextFactory _factory;
    private readonly IntegrationDbContext _db;

    private SabHarness(
        TestIntegrationDbContextFactory factory,
        IntegrationDbContext db,
        SabAdapter adapter,
        ISecretsModule secrets)
    {
        _factory = factory;
        _db = db;
        Adapter = adapter;
        Secrets = secrets;
    }

    public SabAdapter Adapter { get; }

    /// <summary>
    /// The vault, as a substitute. Readable so a test can assert WHICH key was asked for and that
    /// the value was never requested.
    /// </summary>
    public ISecretsModule Secrets { get; }

    /// <summary>Settings naming an entity — the ordinary, correctly configured installation.</summary>
    public static SabSettings ScopedSettings()
        => new() { BaseUrl = "https://sab.example.invalid", Entity = PlaceholderEntity };

    /// <summary>Settings with no entity — the row criterion 2 must refuse at call time.</summary>
    public static SabSettings UnscopedSettings(string? entity = null)
        => new() { BaseUrl = "https://sab.example.invalid", Entity = entity };

    /// <summary>
    /// A tenant whose SAB connection carries <paramref name="settings"/>.
    /// </summary>
    /// <param name="credentialStored">
    /// Whether the vault holds this connection's Open SAB API key. False is not an exotic case: it
    /// is what a connection looks like between being created and having its secret saved, and the
    /// refusal it produces has a different owner from every other refusal here.
    /// </param>
    /// <param name="mode">
    /// <see cref="IntegrationMode.Api"/> by default, which is what Open SAB is;
    /// <see cref="IntegrationMode.Relay"/> is the same installation reached through the on-premise
    /// agent (INT-26) and must change nothing. <see cref="IntegrationMode.Batch"/> is not offered
    /// because <c>SabSettings</c> is not file-based and the settings validator refuses it.
    /// </param>
    public static SabHarness With(
        SabSettings settings,
        bool credentialStored = true,
        IntegrationMode mode = IntegrationMode.Api)
        => Build(settings, credentialStored, mode);

    /// <summary>
    /// A tenant with no SAB connection at all — the case a PORT call must answer by name instead
    /// of throwing, since a port method carries no connection and has to find the row itself. Not
    /// the capability matrix's case any more: <c>CapabilitiesFor</c> is handed its row.
    /// </summary>
    public static SabHarness WithNoConnection()
        => Build(null, credentialStored: true, IntegrationMode.Api);

    /// <summary>
    /// A SAB row carrying another kind's settings, built by hand because the aggregate refuses the
    /// mismatch: <c>IntegrationConnection.Create</c> compares <c>ExpectedKind</c> with the row's
    /// kind. The harness therefore seeds nothing for this case — the test hands the foreign
    /// connection straight to <c>CheckHealthAsync</c>, which takes one explicitly.
    /// </summary>
    public static IntegrationConnection ConnectionCarrying(
        ConnectionSettings settings, IntegrationMode mode = IntegrationMode.Api)
        => IntegrationConnection.Create(
            TenantId,
            IntegrationFamily.CoreBanking,
            settings.ExpectedKind,
            mode,
            "SAB AT — réseau CIF",
            settings,
            Actor,
            TimeProvider.System,
            id: ConnectionId);

    private static SabHarness Build(SabSettings? settings, bool credentialStored, IntegrationMode mode)
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

        var secrets = Substitute.For<ISecretsModule>();

        // The masked value is deliberately not a plausible key: a hint exists to prove presence,
        // and the adapter never reads a value at all.
        var hint = credentialStored
            ? new SecretHint(IntegrationSecretNames.Credential, "****", null)
            : null;

        secrets.GetHintAsync(Arg.Any<SecretKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(hint));

        var adapter = new SabAdapter(
            db,
            new FixedTenantContext(TenantId),
            secrets,
            TimeProvider.System,
            NullLogger<SabAdapter>.Instance);

        return new SabHarness(factory, db, adapter, secrets);
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }
}

/// <summary>
/// The vault names the adapter must use, repeated here rather than referenced.
///
/// <para>
/// <c>IntegrationSecrets</c> is internal to the integration module and visible to the test
/// assembly, so these could be read from it — and they are not, on purpose. A test that computed
/// the expected key with the same helper the adapter uses would assert that the helper equals
/// itself: it would stay green if the scope or the name changed, which is exactly the change that
/// orphans every credential an IMF has already saved.
/// </para>
/// </summary>
internal static class IntegrationSecretNames
{
    public const string Scope = "integration";

    public const string Credential = "connection-credential";
}

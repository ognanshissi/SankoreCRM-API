namespace Sankore.Modules.Integration.Tests.Adapters.Orass;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Adapters.Orass;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;

/// <summary>
/// One or more ORASS connections in a store, a vault that either holds their credentials or does
/// not, and the adapter bound to that tenant.
///
/// <para>
/// The adapter reads its installations from the connection rows — that is how criterion 3's four
/// parameters reach the call path and the capability matrix — so a test of either has to go through
/// a row and not through a constructor argument. This harness is that row, in one place, so the
/// tests assert on outcomes rather than on EF plumbing.
/// </para>
///
/// <para>
/// Everything is fixed, including the connection ids: they are half of the vault keys the
/// credentials are stored under, so a test can assert on the exact <see cref="SecretKey"/> the
/// adapter asked for. A <c>Guid.NewGuid()</c> in a fixture is the one thing that varies between two
/// runs.
/// </para>
///
/// <para>
/// <b>It can seed SEVERAL ORASS connections</b>, which no other adapter harness in this suite needs
/// to. Insurance is the family where a tenant may legitimately have more than one connection of one
/// kind (ASS-01), and the IARD/Vie separation makes two ORASS rows for one insurer group the normal
/// shape — so the fail-closed refusal the PORT methods have to make across every row is only
/// testable with a harness that can produce that shape. The capability matrix no longer needs it:
/// <c>CapabilitiesFor</c> is handed the row it must answer about.
/// </para>
/// </summary>
internal sealed class OrassHarness : IDisposable
{
    public static readonly Guid TenantId = new("99999999-9999-9999-9999-999999999999");

    /// <summary>The first connection seeded. Vault keys are built from it.</summary>
    public static readonly Guid ConnectionId = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    /// <summary>The second, for the several-connections shape only.</summary>
    public static readonly Guid SecondConnectionId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    /// <summary>
    /// An obvious placeholder. <b>No plausible ORASS apporteur code appears anywhere in this
    /// chantier</b>: the condition under test is "is an intermediary code configured", and a
    /// realistic-looking code is the first invented insurer identifier in the repository — the
    /// thing somebody later mistakes for documentation.
    /// </summary>
    public const string PlaceholderIntermediaryCode = "PLACEHOLDER_INTERMEDIARY";

    /// <summary>
    /// A base URL that cannot resolve. <c>.invalid</c> is reserved by RFC 2606 for exactly this, so
    /// a test that one day grew a real transport would fail rather than reach somebody's host.
    /// </summary>
    public const string PlaceholderBaseUrl = "https://orass.example.invalid";

    private static readonly Guid Actor = new("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private readonly TestIntegrationDbContextFactory _factory;
    private readonly IntegrationDbContext _db;

    private OrassHarness(
        TestIntegrationDbContextFactory factory,
        IntegrationDbContext db,
        OrassAdapter adapter,
        ISecretsModule secrets)
    {
        _factory = factory;
        _db = db;
        Adapter = adapter;
        Secrets = secrets;
    }

    public OrassAdapter Adapter { get; }

    /// <summary>
    /// The vault, as a substitute. Readable so a test can assert WHICH keys were asked for and that
    /// no value was ever requested.
    /// </summary>
    public ISecretsModule Secrets { get; }

    /// <summary>
    /// The ordinary, correctly configured API installation: the insurer has opened its API, the
    /// intermediary code is set, and the branch is whatever the caller asks for.
    /// </summary>
    public static OrassSettings ApiSettings(OrassBranch branch = OrassBranch.Iard)
        => new()
        {
            BaseUrl = PlaceholderBaseUrl,
            IntermediaryCode = PlaceholderIntermediaryCode,
            Branch = branch,
        };

    /// <summary>
    /// Criterion 2's own case: no API at this insurer, so the bordereaux carry the business. No
    /// base URL — which IS the statement that nothing was opened.
    /// </summary>
    public static OrassSettings BatchSettings(OrassBranch branch = OrassBranch.Iard)
        => new()
        {
            IntermediaryCode = PlaceholderIntermediaryCode,
            Branch = branch,
            SftpHost = "sftp.example.invalid",
            OutboundDirectory = "/out",
            InboundDirectory = "/in",
        };

    /// <summary>Settings with no intermediary code — the row criterion 3 must refuse at call time.</summary>
    public static OrassSettings UnattributedSettings(string? intermediaryCode = null)
        => new() { BaseUrl = PlaceholderBaseUrl, IntermediaryCode = intermediaryCode };

    /// <summary>
    /// A tenant whose single ORASS connection carries <paramref name="settings"/>.
    /// </summary>
    /// <param name="credentialsStored">
    /// Whether the vault holds everything this connection's carrier needs. False is not an exotic
    /// case: it is what a connection looks like between being created and having its secrets saved,
    /// and the refusal it produces has a different owner from every other refusal here.
    /// </param>
    /// <param name="mode">
    /// <see cref="IntegrationMode.Api"/> by default. <see cref="IntegrationMode.Batch"/> is
    /// criterion 2's fallback, and <see cref="IntegrationMode.Relay"/> is the same insurer reached
    /// through the on-premise agent (INT-26) — a file carrier like <c>Batch</c>, and accepted. The
    /// one shape this adapter reports as incoherent is <c>Api</c> with no base URL: see
    /// <c>OrassCarrierRouting</c>, which also records that Relay used to be reported as a fault and
    /// why it no longer is.
    /// </param>
    public static OrassHarness With(
        OrassSettings settings,
        bool credentialsStored = true,
        IntegrationMode mode = IntegrationMode.Api)
        => Build([(settings, mode)], credentialsStored);

    /// <summary>
    /// A tenant distributing both branches for one insurer group: two ORASS connections, which is
    /// the shape the CIMA separation of IARD and Vie undertakings produces.
    /// </summary>
    public static OrassHarness WithBoth(
        (OrassSettings Settings, IntegrationMode Mode) first,
        (OrassSettings Settings, IntegrationMode Mode) second,
        bool credentialsStored = true)
        => Build([first, second], credentialsStored);

    /// <summary>
    /// A tenant with no ORASS connection at all — the case a PORT call must answer by name instead
    /// of throwing, since a port method carries no connection and has to find the row itself. Not
    /// the capability matrix's case any more: <c>CapabilitiesFor</c> is handed its row.
    /// </summary>
    public static OrassHarness WithNoConnection() => Build([], credentialsStored: true);

    /// <summary>
    /// An ORASS row carrying another kind's settings, built by hand because the aggregate refuses
    /// the mismatch: <c>IntegrationConnection.Create</c> compares <c>ExpectedKind</c> with the row's
    /// kind. The harness therefore seeds nothing for this case — the test hands the foreign
    /// connection straight to <c>CheckHealthAsync</c>, which takes one explicitly.
    /// </summary>
    public static IntegrationConnection ConnectionCarrying(
        ConnectionSettings settings,
        IntegrationMode mode = IntegrationMode.Api,
        Guid? id = null)
        => IntegrationConnection.Create(
            TenantId,
            // Insurance, because that is what an ORASS connection is and what the gateway reads.
            // The family is not derived from the kind anywhere in the module, so the harness has to
            // state it, and stating the wrong one would make a test about the wrong path.
            IntegrationFamily.Insurance,
            settings.ExpectedKind,
            mode,
            "ORASS — assureur partenaire",
            settings,
            Actor,
            TimeProvider.System,
            id: id ?? ConnectionId);

    private static OrassHarness Build(
        IReadOnlyList<(OrassSettings Settings, IntegrationMode Mode)> rows, bool credentialsStored)
    {
        var factory = new TestIntegrationDbContextFactory(TenantId);

        if (rows.Count > 0)
        {
            using var seed = factory.CreateContext();

            for (var i = 0; i < rows.Count; i++)
            {
                seed.Connections.Add(ConnectionCarrying(
                    rows[i].Settings,
                    rows[i].Mode,
                    i == 0 ? ConnectionId : SecondConnectionId));
            }

            seed.SaveChanges();
        }

        // A second context over the same store, the way a request gets its own: the adapter has to
        // read the rows back rather than see the instances the seeding context was tracking.
        var db = factory.CreateContext();

        var secrets = Substitute.For<ISecretsModule>();

        // The masked value is deliberately not a plausible credential: a hint exists to prove
        // presence, and the adapter never reads a value at all. The name is echoed from the key
        // asked for so a test can read a hint back without the substitute having to know which of
        // the three names it is standing in for.
        secrets.GetHintAsync(Arg.Any<SecretKey>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(
                credentialsStored
                    ? new SecretHint(call.Arg<SecretKey>().Name, "****", null)
                    : null));

        var adapter = new OrassAdapter(
            db,
            new FixedTenantContext(TenantId),
            secrets,
            TimeProvider.System,
            NullLogger<OrassAdapter>.Instance);

        return new OrassHarness(factory, db, adapter, secrets);
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
/// itself: it would stay green if the scope or a name changed, which is exactly the change that
/// orphans every credential an IMF has already saved.
/// </para>
/// </summary>
internal static class OrassSecretNames
{
    public const string Scope = "integration";

    public const string Credential = "connection-credential";

    public const string SftpCredential = "sftp-credential";

    public const string SftpHostKeyFingerprint = "sftp-host-key-fingerprint";
}

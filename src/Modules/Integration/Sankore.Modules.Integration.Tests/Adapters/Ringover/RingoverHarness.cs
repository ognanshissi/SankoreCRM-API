namespace Sankore.Modules.Integration.Tests.Adapters.Ringover;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Adapters.Ringover;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;

/// <summary>
/// One Ringover connection in a store, a vault that holds its two secrets or does not, and the
/// adapter bound to that tenant.
///
/// <para>
/// The adapter reads its coordinates from the connection row — that is how the API host reaches
/// the call path and the capability matrix — so a test of either has to go through a row and not
/// through a constructor argument.
/// </para>
///
/// <para>
/// Everything is fixed, including the connection id: it is half of both vault keys, so a test can
/// assert on the exact <see cref="SecretKey"/> values the adapter asked for. A
/// <c>Guid.NewGuid()</c> in a fixture is the one thing that varies between two runs.
/// </para>
/// </summary>
internal sealed class RingoverHarness : IDisposable
{
    public static readonly Guid TenantId = new("66666666-7777-0000-0000-000000000001");

    public static readonly Guid ConnectionId = new("66666666-7777-0000-0000-000000000002");

    /// <summary>
    /// An obvious placeholder host. <b>No plausible Ringover coordinate appears anywhere in this
    /// lot</b>: the condition under test is "is a host configured", and a realistic-looking host
    /// is the first invented provider identifier in the repository — the thing somebody later
    /// mistakes for documentation.
    /// </summary>
    public const string PlaceholderHost = "placeholder-host.invalid";

    private static readonly Guid Actor = new("66666666-7777-0000-0000-000000000003");

    private readonly TestIntegrationDbContextFactory _factory;
    private readonly IntegrationDbContext _db;

    private RingoverHarness(
        TestIntegrationDbContextFactory factory,
        IntegrationDbContext db,
        RingoverAdapter adapter,
        ISecretsModule secrets)
    {
        _factory = factory;
        _db = db;
        Adapter = adapter;
        Secrets = secrets;
    }

    public RingoverAdapter Adapter { get; }

    /// <summary>
    /// The vault, as a substitute. Readable so a test can assert WHICH keys were asked for and
    /// that no value was ever requested.
    /// </summary>
    public ISecretsModule Secrets { get; }

    /// <summary>Settings naming a host — the ordinary, correctly configured connection.</summary>
    public static RingoverSettings ConfiguredSettings()
        => new() { ApiHost = PlaceholderHost, DefaultCallerNumber = "+22507000001" };

    /// <summary>Settings with no host — the row the matrix and the call path must both refuse.</summary>
    public static RingoverSettings HostlessSettings(string? host = null)
        => new() { ApiHost = host };

    /// <param name="apiKeyStored">
    /// Whether the vault holds this connection's API key. False is not an exotic case: it is what
    /// a connection looks like between being created and having its secret saved, and the refusal
    /// it produces has a different owner from the pending account.
    /// </param>
    /// <param name="webhookSecretStored">
    /// Whether the webhook token is stored. Separate from the key because they break different
    /// halves — without the token the provider's events are refused at the door, which looks to
    /// an agent like a completely different fault.
    /// </param>
    public static RingoverHarness With(
        RingoverSettings settings,
        bool apiKeyStored = true,
        bool webhookSecretStored = true,
        IntegrationMode mode = IntegrationMode.Api)
        => Build(settings, apiKeyStored, webhookSecretStored, mode);

    /// <summary>
    /// A tenant with no Ringover connection at all — the case a PORT call must answer by name
    /// instead of throwing, since a port method carries no connection and has to find the row
    /// itself.
    /// </summary>
    public static RingoverHarness WithNoConnection()
        => Build(null, true, true, IntegrationMode.Api);

    /// <summary>
    /// A Ringover row carrying another kind's settings, built by hand because the aggregate
    /// refuses the mismatch: <c>IntegrationConnection.Create</c> compares <c>ExpectedKind</c> with
    /// the row's kind. The test hands the foreign connection straight to <c>CheckHealthAsync</c>,
    /// which takes one explicitly.
    /// </summary>
    public static IntegrationConnection ConnectionCarrying(
        ConnectionSettings settings, IntegrationMode mode = IntegrationMode.Api)
        => IntegrationConnection.Create(
            TenantId,
            settings is RingoverSettings
                ? IntegrationFamily.Telephony
                : IntegrationFamily.CoreBanking,
            settings.ExpectedKind,
            mode,
            "Ringover — téléphonie",
            settings,
            Actor,
            TimeProvider.System,
            id: ConnectionId);

    private static RingoverHarness Build(
        RingoverSettings? settings, bool apiKeyStored, bool webhookSecretStored, IntegrationMode mode)
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

        // Keyed by the secret NAME, so the two halves can be present independently — which is the
        // whole point of telling the two refusals apart. The masked value is deliberately not a
        // plausible key: a hint exists to prove presence, and the adapter never reads a value.
        secrets.GetHintAsync(Arg.Any<SecretKey>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var key = call.ArgAt<SecretKey>(0);

                var stored = key.Name switch
                {
                    RingoverSecretNames.Credential => apiKeyStored,
                    RingoverSecretNames.WebhookSecret => webhookSecretStored,
                    _ => false,
                };

                return Task.FromResult<SecretHint?>(
                    stored ? new SecretHint(key.Name, "****", null) : null);
            });

        var adapter = new RingoverAdapter(
            db,
            new FixedTenantContext(TenantId),
            secrets,
            TimeProvider.System,
            NullLogger<RingoverAdapter>.Instance);

        return new RingoverHarness(factory, db, adapter, secrets);
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
internal static class RingoverSecretNames
{
    public const string Scope = "integration";

    public const string Credential = "connection-credential";

    public const string WebhookSecret = "webhook-secret";
}

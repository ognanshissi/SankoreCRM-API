namespace Sankore.Modules.Integration.Tests.Features.Connections;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;

/// <summary>
/// Doubles and builders shared by the connection slice tests. Everything is built with
/// <c>new</c>: no container, no host, no MediatR pipeline — a handler test that needed DI would
/// be testing the wiring instead of the decision.
/// </summary>
internal static class ConnectionsTestHarness
{
    internal static readonly DateTimeOffset Now = new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);

    internal static readonly Guid Actor = Guid.Parse("44444444-4444-4444-4444-444444444444");

    internal static TimeProvider Clock(DateTimeOffset? at = null) => new FixedClock(at ?? Now);

    internal static ICurrentUser User(Guid tenantId, Guid? userId = null)
        => new TestCurrentUser(userId ?? Actor, tenantId);

    internal static NullLogger<T> Log<T>() => NullLogger<T>.Instance;

    internal static TemenosSettings Temenos(string baseUrl = "https://cbs.example.ci/api/")
        => new()
        {
            BaseUrl = baseUrl,
            AuthMode = TemenosAuthMode.StaticToken,
            CompanyId = "CI0010001",
        };

    internal static AmplitudeSettings Amplitude(AmplitudeVersion version = AmplitudeVersion.Legacy)
        => new() { AmplitudeVersion = version };

    internal static OrassSettings Orass() => new() { IntermediaryCode = "INT-0042" };

    /// <summary>
    /// Seeds one connection, optionally already health-checked and active. Written through the
    /// context it is given, so a test can seed a FOREIGN tenant's row through its own context —
    /// EF's query filters apply to reads, not to inserts.
    /// </summary>
    internal static IntegrationConnection Seed(
        IntegrationDbContext db,
        Guid tenantId,
        IntegrationFamily family = IntegrationFamily.CoreBanking,
        IntegrationKind kind = IntegrationKind.Temenos,
        IntegrationMode mode = IntegrationMode.Api,
        string name = "CBS principal",
        ConnectionSettings? settings = null,
        bool healthy = false,
        bool active = false)
    {
        var clock = Clock();

        var connection = IntegrationConnection.Create(
            tenantId: tenantId,
            family: family,
            kind: kind,
            mode: mode,
            name: name,
            settings: settings ?? DefaultSettingsFor(kind),
            createdBy: Actor,
            clock: clock);

        if (healthy)
            connection.RecordHealth(IntegrationHealth.Healthy(TimeSpan.FromMilliseconds(80), Now), clock);

        if (active)
            connection.Activate(Actor, clock);

        db.Connections.Add(connection);
        db.SaveChanges();

        return connection;
    }

    private static ConnectionSettings DefaultSettingsFor(IntegrationKind kind) => kind switch
    {
        IntegrationKind.Temenos => Temenos(),
        IntegrationKind.Amplitude => Amplitude(),
        IntegrationKind.Sab => new SabSettings { BaseUrl = "https://sab.example.ci/", Entity = "CIF01" },
        IntegrationKind.PerfectVision => new PerfectVisionSettings(),
        IntegrationKind.Orass => Orass(),
        _ => new FakeSettings(),
    };

    /// <summary>
    /// An <see cref="IntegrationAdapterResolver"/> whose keyed lookups answer from a plain
    /// dictionary. The resolver reads adapters through <c>GetKeyedService</c>, so a service
    /// provider is unavoidable — this is the smallest honest one, and no container is built.
    /// </summary>
    internal static IntegrationAdapterResolver Resolver(
        IntegrationDbContext db, params ICbsAdapter[] adapters)
        => new(db, new KeyedAdapterProvider(adapters.ToDictionary(a => a.Kind.ToString())));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record TestCurrentUser(Guid Id, Guid TenantId) : ICurrentUser
    {
        public string DisplayName => "test";

        public bool IsAuthenticated => true;

        public IReadOnlyList<string> Roles => ["Administrator"];
    }

    private sealed class KeyedAdapterProvider(Dictionary<string, ICbsAdapter> adapters)
        : IServiceProvider, IKeyedServiceProvider
    {
        public object? GetService(Type serviceType) => null;

        public object? GetKeyedService(Type serviceType, object? serviceKey)
            => serviceKey is string key && adapters.TryGetValue(key, out var adapter) ? adapter : null;

        public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
            => GetKeyedService(serviceType, serviceKey)
               ?? throw new InvalidOperationException($"No adapter keyed {serviceKey}.");
    }
}

/// <summary>
/// A <see cref="ICbsAdapter"/> that answers whatever the test tells it to, and counts its calls.
/// The Fake adapter assembly is deliberately not used here: these tests are about what the
/// handler does with an answer, not about what any real adapter answers.
/// </summary>
internal sealed class StubCbsAdapter(
    IntegrationKind kind,
    IntegrationHealth? health = null,
    Exception? throws = null) : ICbsAdapter
{
    public IntegrationKind Kind { get; } = kind;

    public IntegrationCapabilities Capabilities { get; } = IntegrationCapabilities.None;

    public int Calls { get; private set; }

    public Task<IntegrationHealth> CheckHealthAsync(
        IntegrationConnection connection, CancellationToken ct)
    {
        Calls++;

        if (throws is not null) throw throws;

        return Task.FromResult(
            health ?? IntegrationHealth.Healthy(
                TimeSpan.FromMilliseconds(42), ConnectionsTestHarness.Now));
    }
}

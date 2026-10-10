namespace Sankore.Modules.Integration.Tests.Adapters.Temenos;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Integration.Adapters.Temenos;
using Sankore.Modules.Integration.Infrastructure.Resilience;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Mappings;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.CallLog;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;

/// <summary>
/// Builds a <c>TemenosAdapter</c> wired to a <see cref="TemenosStubTransport"/>, with a real
/// <c>IntegrationDbContext</c>, a real <c>MappingResolver</c>, a real token cache and a recording
/// call journal.
///
/// <para>
/// <b>Real where it can be, double only at the edges.</b> The database is EF InMemory under a
/// genuine <c>FixedTenantContext</c>, so the adapter's own tenant predicate is exercised rather
/// than bypassed; the mapping table holds rows, so INT-13's "an absent code produces a technical
/// error" is a real lookup miss and not a stubbed return; the token cache is the shipped one, so
/// the renewal and single-flight behaviour under test is the behaviour that ships. Only the HTTP
/// transport and the secrets vault are doubles, because one is a network and the other is a
/// database we do not need.
/// </para>
///
/// <para>
/// Disposable, and each instance owns its own InMemory database: the contract suite builds a
/// fresh port per fact, and a shared store would let the account one test opens change what the
/// next test lists.
/// </para>
/// </summary>
internal sealed class TemenosTestHarness : IDisposable
{
    public static readonly Guid TenantId = new("aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa");

    public static readonly Guid ConnectionId = new("bbbbbbbb-2222-2222-2222-bbbbbbbbbbbb");

    private static readonly Guid Actor = new("cccccccc-3333-3333-3333-cccccccccccc");

    private readonly TestIntegrationDbContextFactory _databases = new(TenantId);
    private readonly IntegrationDbContext _db;

    private TemenosTestHarness(
        TemenosSettings settings,
        bool active,
        string? credential,
        bool withMappings,
        TemenosAdapterOptions options,
        TimeProvider clock)
    {
        _db = _databases.CreateContext();

        Transport = new TemenosStubTransport();
        Journal = new RecordingCallJournal();
        Clock = clock;
        Options = Microsoft.Extensions.Options.Options.Create(options);

        Connection = IntegrationConnection.Create(
            tenantId: TenantId,
            family: IntegrationFamily.CoreBanking,
            kind: IntegrationKind.Temenos,
            mode: IntegrationMode.Api,
            name: "Transact CI",
            settings: settings,
            createdBy: Actor,
            clock: TimeProvider.System,
            id: ConnectionId);

        if (active)
        {
            // A connection may only be activated after a health check has passed (INT-03), and
            // the aggregate enforces it — so the fixture records one rather than reaching past
            // the invariant.
            Connection.RecordHealth(
                IntegrationHealth.Healthy(TimeSpan.FromMilliseconds(12), TimeProvider.System.GetUtcNow()),
                TimeProvider.System);

            Connection.Activate(Actor, TimeProvider.System);
        }

        _db.Connections.Add(Connection);

        if (withMappings) SeedMappings();

        _db.SaveChanges();

        Secrets = Substitute.For<ISecretsModule>();
        Secrets.GetValueAsync(Arg.Any<SecretKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(credential));

        var factory = new StubHttpClientFactory(Transport);

        TokenCache = new TemenosTokenCache(Clock, Options);

        var authenticator = new TemenosAuthenticator(
            factory, Secrets, TokenCache, Clock, Options,
            NullLogger<TemenosAuthenticator>.Instance);

        // Its own provider per harness, so a circuit opened by one test's forced failures can
        // never leak into the next one's — a shared singleton would make the suite order-dependent
        // in the least obvious way possible. Exposed so a test can read the breaker state back,
        // which is the half of INT-09 that only becomes true once a real adapter runs inside it.
        Pipelines = new IntegrationResiliencePipelineProvider();

        var transport = new TemenosTransport(
            factory, authenticator, Pipelines,
            Options, NullLogger<TemenosTransport>.Instance);

        Adapter = new TemenosAdapter(
            _db,
            new FixedTenantContext(TenantId),
            transport,
            new TemenosCodeTranslation(new MappingResolver(_db)),
            Journal,
            Options,
            Clock,
            NullLogger<TemenosAdapter>.Instance);
    }

    public TemenosStubTransport Transport { get; }

    public RecordingCallJournal Journal { get; }

    public TemenosAdapter Adapter { get; }

    public IntegrationConnection Connection { get; }

    public ISecretsModule Secrets { get; }

    public TemenosTokenCache TokenCache { get; }

    /// <summary>
    /// The per-connection Polly pipelines this harness's transport runs inside. Readable so a test
    /// can assert the circuit state — the observable half of INT-09's criterion 4.
    /// </summary>
    public IntegrationResiliencePipelineProvider Pipelines { get; }

    public IOptions<TemenosAdapterOptions> Options { get; }

    public TimeProvider Clock { get; }

    /// <summary>
    /// The ordinary case: an active OAuth connection, a credential in the vault, and a complete
    /// mapping table.
    /// </summary>
    public static TemenosTestHarness Create(
        TemenosSettings? settings = null,
        bool active = true,
        string? credential = TemenosFixtures.ClientSecret,
        bool withMappings = true,
        TemenosAdapterOptions? options = null,
        TimeProvider? clock = null)
        => new(
            settings ?? DefaultSettings(),
            active,
            credential,
            withMappings,
            options ?? new TemenosAdapterOptions(),
            clock ?? TimeProvider.System);

    /// <summary>The same, on a connection configured with a long-lived token instead of OAuth.</summary>
    public static TemenosTestHarness WithStaticToken(string token = "static-token")
        => new(
            DefaultSettings() with { AuthMode = TemenosAuthMode.StaticToken, TokenEndpoint = null },
            active: true,
            credential: token,
            withMappings: true,
            new TemenosAdapterOptions(),
            TimeProvider.System);

    public static TemenosSettings DefaultSettings() => new()
    {
        BaseUrl = TemenosFixtures.BaseUrl,
        AuthMode = TemenosAuthMode.OAuthClientCredentials,
        TokenEndpoint = TemenosFixtures.TokenEndpoint,
        OAuthClientId = TemenosFixtures.ClientId,
        OAuthScope = "transact.party transact.holdings",
        CompanyId = TemenosFixtures.CompanyId,
        ApiVersion = TemenosFixtures.ApiVersion,
    };

    /// <summary>
    /// The eight translations a complete customer payload needs, plus the product one INT-13
    /// turns on. Written as rows in the real table because the behaviour under test is a lookup
    /// miss, and a stubbed resolver could not miss.
    /// </summary>
    private void SeedMappings()
    {
        void Map(MappingDomain domain, string crm, string external) =>
            _db.Mappings.Add(IntegrationMapping.Create(
                TenantId, ConnectionId, domain, crm, external, Actor, TimeProvider.System));

        Map(MappingDomain.Product, TemenosFixtures.SeededCrmProductCode, TemenosFixtures.SeededExternalProductCode);
        Map(MappingDomain.Gender, "F", "FEMALE");
        Map(MappingDomain.MaritalStatus, "Married", "MARRIED");
        Map(MappingDomain.Country, "CIV", "CI");
        Map(MappingDomain.Country, "CI", "CI");
        Map(MappingDomain.IdDocType, "CNI", "NATIONAL.ID");
        Map(MappingDomain.Profession, "Commerçante", "TRADER");
        Map(MappingDomain.Sector, "Commerce", "1100");
        Map(MappingDomain.Agency, "AG-ABJ-01", TemenosFixtures.CompanyId);
    }

    public void Dispose()
    {
        _db.Dispose();
        _databases.Dispose();
        Transport.Dispose();
    }
}

/// <summary>
/// An <see cref="IHttpClientFactory"/> over one handler.
///
/// <para>
/// A fresh <see cref="HttpClient"/> per call, as the real factory does, and with
/// <c>disposeHandler: false</c> so the stub survives the client being disposed — the adapter
/// creates one client per request and the stub's counters must outlive all of them.
/// </para>
/// </summary>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
    {
        // Mirrors what TemenosAdapterRegistration configures: the per-call budget is a linked
        // CancellationToken in the transport, not this.
        Timeout = Timeout.InfiniteTimeSpan,
    };
}

/// <summary>
/// The call journal, recording instead of writing (INT-08, criterion 1).
///
/// <para>
/// A real implementation rather than an NSubstitute mock, because what has to be asserted is not
/// "was it called" but "did a row describe the outcome": the family, the code and the HTTP status
/// the transport wrote onto the probe. It also passes the result through verbatim, which is the
/// shipped journal's own contract — a double that swallowed a failure would make every adapter
/// test pass.
/// </para>
/// </summary>
internal sealed class RecordingCallJournal : ICallJournal
{
    public List<JournalRow> Rows { get; } = [];

    public async Task<IntegrationResult<T>> RecordAsync<T>(
        CallContext context, Func<CancellationToken, Task<IntegrationResult<T>>> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);

        var result = await operation(ct);
        Append(context, result);

        return result;
    }

    public async Task<IntegrationResult> RecordAsync(
        CallContext context, Func<CancellationToken, Task<IntegrationResult>> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);

        var result = await operation(ct);
        Append(context, result);

        return result;
    }

    private void Append(CallContext context, IntegrationResult result) => Rows.Add(new JournalRow(
        context.Operation,
        context.TenantId,
        context.ConnectionId,
        context.Endpoint,
        context.Correlation,
        result.IsSuccess,
        result.Family,
        result.Code,
        context.Probe.HttpStatus));
}

internal sealed record JournalRow(
    string Operation,
    Guid TenantId,
    Guid ConnectionId,
    string? Endpoint,
    string Correlation,
    bool Success,
    ErrorFamily? Family,
    string? Code,
    int? HttpStatus);

/// <summary>
/// A clock a test can move.
///
/// <para>
/// Hand-written because <c>Microsoft.Extensions.TimeProvider.Testing</c> is not referenced
/// anywhere in this repository, and the token cache's expiry arithmetic cannot be exercised
/// against a clock that only moves forward by itself — a test that waited for a real token to
/// expire would take an hour.
/// </para>
/// </summary>
internal sealed class MovableClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public static MovableClock At(int year, int month, int day)
        => new(new DateTimeOffset(year, month, day, 9, 0, 0, TimeSpan.Zero));

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

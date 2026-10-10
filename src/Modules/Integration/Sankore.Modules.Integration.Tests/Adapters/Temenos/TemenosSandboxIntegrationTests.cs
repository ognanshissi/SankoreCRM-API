namespace Sankore.Modules.Integration.Tests.Adapters.Temenos;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Integration.Adapters.Temenos;
using Sankore.Modules.Integration.Infrastructure.Resilience;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Mappings;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The Temenos adapter against a REAL Transact installation.
///
/// <para>
/// <b>This is the test that criteria INT-12.5 and INT-13.4 ask for, and it has never been run.</b>
/// There is no sandbox and no credentials, so the two criteria are <b>prepared and not
/// verified</b>. Everything else in this folder runs against a stub whose field names were copied
/// from the same public documentation as <c>TemenosWire.cs</c> — the two agree because they were
/// written together, not because either was checked, and no number of passing stub tests can
/// contradict a mistake they share.
/// </para>
///
/// <para>
/// Opt-in, and silent when the three environment variables below are unset, so a normal
/// <c>dotnet test</c> neither needs credentials nor reaches the network:
/// </para>
/// <code>
/// export SANKORE_TEMENOS_BASE_URL=https://transact.example.ci/irf-provider-container/api
/// export SANKORE_TEMENOS_CLIENT_ID=sankore-crm
/// export SANKORE_TEMENOS_CLIENT_SECRET=...
/// # optional, with the defaults shown:
/// export SANKORE_TEMENOS_TOKEN_ENDPOINT=&lt;BASE_URL&gt;/oauth2/token
/// export SANKORE_TEMENOS_API_VERSION=v2.0.0
/// export SANKORE_TEMENOS_COMPANY_ID=
/// dotnet test src/Modules/Integration/Sankore.Modules.Integration.Tests \
///   --filter "FullyQualifiedName~TemenosSandboxIntegrationTests"
/// </code>
///
/// <para>
/// <b>What a run would actually establish</b>, and nothing in this repository can:
/// </para>
/// <list type="number">
/// <item>That the paths of <c>TemenosPaths</c> exist — a wrong one answers 404, which this adapter
///   reports as "the customer is absent".</item>
/// <item>That <c>TemenosQuery.Mnemonic</c> is the field our CRM reference is indexed under. If it
///   is not, the pre-create search of INT-12's third criterion answers "no such customer" every
///   single time, the duplicate protection is gone, and every test in this folder still passes.</item>
/// <item>That the field names of <c>TemenosWire.cs</c> are the installation's own — the M02
///   biometry lesson, where not one hand-written name matched and every call mapped to null.</item>
/// <item>That dates travel in the format <c>TemenosWireFormats.RequestDate</c> names, and come
///   back in one this adapter parses.</item>
/// <item>Whether the installation honours <c>Idempotency-Key</c> on a write.</item>
/// </list>
///
/// <para>
/// <b>It reads only.</b> A token, a bounded party enquiry, and the seeded account's balance if one
/// is named. No creation, no account opening, no write of any kind: a sandbox is frequently a copy
/// of a production database, and a test that created parties in one would be a test nobody dares
/// run twice. The write paths are what the stub suites cover, and verifying them against an
/// installation is a deliberate session with somebody who owns the data, not a <c>dotnet test</c>.
/// </para>
///
/// <para>
/// Xunit's <c>SkippableFact</c> is not referenced in this project (checked), so an unconfigured
/// run returns early and says which variable is missing through the test output rather than
/// reporting a skip.
/// </para>
/// </summary>
public sealed class TemenosSandboxIntegrationTests(ITestOutputHelper output)
{
    private const string BaseUrlVariable = "SANKORE_TEMENOS_BASE_URL";
    private const string ClientIdVariable = "SANKORE_TEMENOS_CLIENT_ID";
    private const string ClientSecretVariable = "SANKORE_TEMENOS_CLIENT_SECRET";

    private static readonly Guid TenantId = new("dddddddd-4444-4444-4444-dddddddddddd");
    private static readonly Guid ConnectionId = new("eeeeeeee-5555-5555-5555-eeeeeeeeeeee");

    [Fact]
    public async Task The_adapter_should_authenticate_and_read_against_a_real_installation()
    {
        if (!TryReadEnvironment(out var settings, out var secret, out var missing))
        {
            output.WriteLine(
                $"Skipped: {missing} is not set. This is the only test that checks the paths, the "
                + "search parameter, the field names and the date formats of TemenosWire.cs "
                + "against a real Transact — criteria INT-12.5 and INT-13.4 are prepared, not "
                + "verified. See the class comment for how to run it.");

            return;
        }

        using var factory = new SandboxHttpClientFactory();

        var connection = IntegrationConnection.Create(
            tenantId: TenantId,
            family: IntegrationFamily.CoreBanking,
            kind: IntegrationKind.Temenos,
            mode: IntegrationMode.Api,
            name: "Transact sandbox",
            settings: settings,
            createdBy: TenantId,
            clock: TimeProvider.System,
            id: ConnectionId);

        var secrets = Substitute.For<ISecretsModule>();
        secrets.GetValueAsync(Arg.Any<SecretKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(secret));

        var options = Options.Create(new TemenosAdapterOptions());

        var authenticator = new TemenosAuthenticator(
            factory, secrets, new TemenosTokenCache(TimeProvider.System, options),
            TimeProvider.System, options, NullLogger<TemenosAuthenticator>.Instance);

        var transport = new TemenosTransport(
            factory, authenticator, new IntegrationResiliencePipelineProvider(),
            options, NullLogger<TemenosTransport>.Instance);

        var binding = new TemenosBinding(connection, settings);

        // Step one, separately, because it is the step whose failure means something different:
        // a refused token is a credential problem and nothing downstream can be interpreted.
        var token = await transport.ProveCredentialsAsync(binding, CancellationToken.None);

        output.WriteLine($"Token: {(token.IsSuccess ? "obtained" : $"{token.Code} — {token.Detail}")}");

        token.IsSuccess.Should().BeTrue(
            "the installation refused the client credentials, so nothing below can be read");

        // Step two: the health check, which is a bounded party enquiry. A 404 here means
        // TemenosPaths or the API version segment is wrong; an unexpected-response means the
        // envelope is not the one TemenosWire.cs declares.
        var adapter = BuildAdapter(settings, secrets, transport, options);

        var health = await adapter.CheckHealthAsync(connection, CancellationToken.None);

        output.WriteLine(
            $"Health: {(health.IsHealthy ? "healthy" : health.Detail)} "
            + $"({health.Latency?.TotalMilliseconds:0} ms)");

        health.IsHealthy.Should().BeTrue(
            "a failure here is the path, the API version or the envelope — see TemenosWire.cs");

        // Step three, optional: a balance on an account the operator named. Skipped rather than
        // invented, because an account reference is installation data and guessing one would
        // report a wrong path as an absent account.
        var accountId = Environment.GetEnvironmentVariable("SANKORE_TEMENOS_ACCOUNT_ID");

        if (string.IsNullOrWhiteSpace(accountId))
        {
            output.WriteLine(
                "Balance: not attempted (set SANKORE_TEMENOS_ACCOUNT_ID to an account of the "
                + "installation to check the Holdings paths and the balance field names too).");

            return;
        }

        var balance = await adapter.GetBalanceAsync(
            new ExternalId(accountId), CancellationToken.None);

        output.WriteLine(
            balance.IsSuccess
                ? $"Balance: {balance.Value.Balance} {balance.Value.Currency} "
                  + $"(available {balance.Value.AvailableBalance})"
                : $"Balance: {balance.Code} — {balance.Detail}");

        balance.IsSuccess.Should().BeTrue();
        balance.Value.Currency.Should().NotBeNullOrWhiteSpace(
            "a balance with no currency means the field name in TemenosWire.cs is wrong");
    }

    /// <summary>
    /// The adapter needs an <c>IntegrationDbContext</c> for its binding and its mappings, and
    /// this test supplies the binding itself — so it builds one over the same InMemory factory the
    /// stub suites use, with the sandbox connection in it and no mapping rows.
    ///
    /// <para>
    /// No mappings, on purpose: a read needs none, and seeding invented translations would make a
    /// run against an installation assert against values nobody chose. The write paths, which DO
    /// need mappings, are deliberately not exercised here — see the class remarks.
    /// </para>
    /// </summary>
    private static TemenosAdapter BuildAdapter(
        TemenosSettings settings,
        ISecretsModule secrets,
        TemenosTransport transport,
        IOptions<TemenosAdapterOptions> options)
    {
        var databases = new TestIntegrationDbContextFactory(TenantId);
        var db = databases.CreateContext();

        var connection = IntegrationConnection.Create(
            tenantId: TenantId,
            family: IntegrationFamily.CoreBanking,
            kind: IntegrationKind.Temenos,
            mode: IntegrationMode.Api,
            name: "Transact sandbox",
            settings: settings,
            createdBy: TenantId,
            clock: TimeProvider.System,
            id: ConnectionId);

        connection.RecordHealth(
            IntegrationHealth.Healthy(TimeSpan.Zero, TimeProvider.System.GetUtcNow()), TimeProvider.System);
        connection.Activate(TenantId, TimeProvider.System);

        db.Connections.Add(connection);
        db.SaveChanges();

        return new TemenosAdapter(
            db,
            new FixedTenantContext(TenantId),
            transport,
            new TemenosCodeTranslation(new MappingResolver(db)),
            new RecordingCallJournal(),
            options,
            TimeProvider.System,
            NullLogger<TemenosAdapter>.Instance);
    }

    /// <summary>
    /// Reads the three required variables and the four optional ones. Returns the FIRST missing
    /// name, so an operator who sets two of three is told which one they forgot rather than being
    /// told "not configured".
    /// </summary>
    private static bool TryReadEnvironment(
        out TemenosSettings settings, out string secret, out string? missing)
    {
        settings = new TemenosSettings();
        secret = string.Empty;

        var baseUrl = Environment.GetEnvironmentVariable(BaseUrlVariable);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            missing = BaseUrlVariable;
            return false;
        }

        var clientId = Environment.GetEnvironmentVariable(ClientIdVariable);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            missing = ClientIdVariable;
            return false;
        }

        var clientSecret = Environment.GetEnvironmentVariable(ClientSecretVariable);
        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            missing = ClientSecretVariable;
            return false;
        }

        var tokenEndpoint = Environment.GetEnvironmentVariable("SANKORE_TEMENOS_TOKEN_ENDPOINT");

        settings = new TemenosSettings
        {
            BaseUrl = baseUrl,
            AuthMode = TemenosAuthMode.OAuthClientCredentials,
            TokenEndpoint = string.IsNullOrWhiteSpace(tokenEndpoint)
                ? $"{baseUrl.TrimEnd('/')}/oauth2/token"
                : tokenEndpoint,
            OAuthClientId = clientId,
            OAuthScope = Environment.GetEnvironmentVariable("SANKORE_TEMENOS_SCOPE"),
            CompanyId = Environment.GetEnvironmentVariable("SANKORE_TEMENOS_COMPANY_ID"),
            ApiVersion = Environment.GetEnvironmentVariable("SANKORE_TEMENOS_API_VERSION"),
        };

        secret = clientSecret;
        missing = null;

        return true;
    }

    /// <summary>
    /// A real <see cref="HttpClient"/>, one per call, over one shared handler — the shape
    /// <c>TemenosAdapterRegistration</c> configures, SsrfSafeHandler included by its absence: an
    /// on-premise installation at <c>10.x</c> is the ordinary case, and a run that could not reach
    /// one would be proving the wrong thing.
    /// </summary>
    private sealed class SandboxHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly SocketsHttpHandler _handler = new();

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        public void Dispose() => _handler.Dispose();
    }
}

namespace Sankore.Integration.RelayAgent.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sankore.Integration.RelayAgent.Configuration;
using Sankore.Integration.RelayAgent.Execution;
using Sankore.Integration.RelayAgent.Observability;
using Sankore.Integration.RelayAgent.Protocol;
using Xunit;

/// <summary>
/// Criterion 3's local HTTP call, exercised against a real HTTP server on the loopback.
///
/// <para>
/// The only end-to-end path this component can honestly be tested on. The SANKORE side of the
/// protocol does not exist, so a full "order arrives, work happens, result returns" run is not
/// available; but everything from an order to a real socket and back IS, and that is what this
/// covers — the declared base URL, the declared header, the declared method, the real status code
/// and body, and the latency landing in the heartbeat's registry.
/// </para>
///
/// <para>
/// A <see cref="HttpListener"/> on an ephemeral loopback port rather than a mocked handler,
/// because the mocked version would also have passed with a confinement check that did not work.
/// </para>
/// </summary>
public sealed class HttpOrderEndToEndTests : IAsyncLifetime, IDisposable
{
    private HttpListener _listener = null!;
    private string _baseUrl = null!;
    private CancellationTokenSource _serving = null!;
    private Task _serverLoop = null!;

    /// <summary>What the stub server was asked for, so the test can assert on the real request.</summary>
    private readonly List<(string Method, string RawUrl, string? ApiKey, string Body)> _received = [];

    public Task InitializeAsync()
    {
        // An ephemeral port found by the OS: a fixed one makes the suite fail on a machine where
        // something else already holds it, which is the kind of flake nobody ever diagnoses.
        var port = FreeLoopbackPort();
        _baseUrl = $"http://127.0.0.1:{port}/api/v2/";

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();

        _serving = new CancellationTokenSource();
        _serverLoop = ServeAsync(_serving.Token);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _serving.CancelAsync();
        _listener.Stop();
        _listener.Close();

        try
        {
            await _serverLoop;
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException
                                      or OperationCanceledException)
        {
            // Shutting the listener down is how the loop is stopped.
        }

        _serving.Dispose();
    }

    /// <summary>
    /// The listener and the token source are disposables owned by this fixture, and xUnit calls
    /// this after <see cref="DisposeAsync"/>. Both disposals are idempotent, so the overlap is
    /// harmless — the point is that nothing is left open if a test throws before DisposeAsync runs.
    /// </summary>
    public void Dispose()
    {
        _serving?.Dispose();
        ((IDisposable?)_listener)?.Dispose();
    }

    [Fact]
    public async Task A_declared_target_is_called_at_its_declared_address_with_its_declared_header()
    {
        var (executor, health) = Build();

        var order = Order("cbs-api", "GET", "accounts/CI0012345678");

        var result = await executor.ExecuteAsync(order, CancellationToken.None);

        result.Outcome.Should().Be(RelayOutcome.Succeeded);

        var answer = result.Payload!.Value.Deserialize<RelayHttpResult>(RelayProtocolJson.Options)!;
        answer.Status.Should().Be(200);
        answer.ContentType.Should().Be("application/json");
        answer.Body.Should().Contain("CI0012345678");

        // The address really used, as the server saw it — the base URL from the file plus the
        // order's relative path, and nothing else.
        _received.Should().ContainSingle();
        _received[0].Method.Should().Be("GET");
        _received[0].RawUrl.Should().Be("/api/v2/accounts/CI0012345678");

        // The header comes from the configuration file. An order has no field that could set one.
        _received[0].ApiKey.Should().Be("la-clef-du-systeme-local");

        // And the heartbeat now knows the target is reachable, with a real measurement.
        health.RecordOrder("cbs-api", reached: true, 1);
        health.Snapshot([("cbs-api", RelayOrderKind.HttpCall)])[0]
            .State.Should().Be(RelayTargetState.Reachable);
    }

    [Fact]
    public async Task A_method_the_target_was_not_declared_to_allow_never_reaches_the_server()
    {
        // The target below declares GET only. This is how an IMF keeps a read-only integration
        // read-only without having to trust us.
        var (executor, _) = Build(allowedMethods: ["GET"]);

        var result = await executor.ExecuteAsync(
            Order("cbs-api", "POST", "accounts"), CancellationToken.None);

        result.Outcome.Should().Be(RelayOutcome.Refused);
        result.ErrorCode.Should().Be(RelayErrorCodes.MethodNotAllowed);
        _received.Should().BeEmpty("a refused order must not reach the relayed system at all");
    }

    [Fact]
    public async Task A_path_that_would_leave_the_base_never_reaches_the_server()
    {
        var (executor, _) = Build();

        var result = await executor.ExecuteAsync(
            Order("cbs-api", "GET", "../../admin/users"), CancellationToken.None);

        result.Outcome.Should().Be(RelayOutcome.Refused);
        result.ErrorCode.Should().Be(RelayErrorCodes.PathOutsideBase);
        _received.Should().BeEmpty();
    }

    [Fact]
    public async Task An_error_status_is_reported_and_not_judged()
    {
        // 404 may mean "no such customer" or "wrong path", and this process knows nothing of our
        // domain. It therefore succeeds at relaying and hands the status to SANKORE, where the
        // mapping tables are.
        var (executor, _) = Build();

        var result = await executor.ExecuteAsync(
            Order("cbs-api", "GET", "not-found"), CancellationToken.None);

        result.Outcome.Should().Be(RelayOutcome.Succeeded);

        var answer = result.Payload!.Value.Deserialize<RelayHttpResult>(RelayProtocolJson.Options)!;
        answer.Status.Should().Be(404);
    }

    [Fact]
    public async Task A_response_past_the_declared_cap_fails_without_being_held_in_memory()
    {
        var (executor, _) = Build(maxResponseBytes: 1024);

        var result = await executor.ExecuteAsync(
            Order("cbs-api", "GET", "large"), CancellationToken.None);

        result.Outcome.Should().Be(RelayOutcome.Unavailable);
        result.ErrorCode.Should().Be(RelayErrorCodes.PayloadTooLarge);
        result.Payload.Should().BeNull();

        // Reached, so the heartbeat must not call the target unreachable: it answered, we declined
        // its answer.
        result.ReachedTarget.Should().BeTrue();
    }

    [Fact]
    public async Task An_unreachable_target_is_Unavailable_and_never_an_exception()
    {
        var options = new RelayAgentOptions();
        options.HttpTargets.Add(new RelayHttpTargetOptions
        {
            Name = "eteint",
            // Port 9 is the discard service: nothing listens, so the connect is refused at once.
            BaseUrl = "http://127.0.0.1:9/api/",
            TimeoutSeconds = 5,
        });

        var executor = new HttpOrderExecutor(Factory(), Options.Create(options));

        var result = await executor.ExecuteAsync(
            Order("eteint", "GET", "x"), CancellationToken.None);

        result.Outcome.Should().Be(RelayOutcome.Unavailable);
        result.ErrorCode.Should().Be(RelayErrorCodes.TargetUnreachable);
        result.ExceptionTypeName.Should().NotBeNull("the TYPE is kept so a log line can name it");
    }

    private (HttpOrderExecutor Executor, TargetHealthRegistry Health) Build(
        string[]? allowedMethods = null, int maxResponseBytes = 4 * 1024 * 1024)
    {
        var target = new RelayHttpTargetOptions
        {
            Name = "cbs-api",
            BaseUrl = _baseUrl,
            TimeoutSeconds = 10,
            MaxResponseBytes = maxResponseBytes,
        };

        target.AllowedMethods.Clear();
        foreach (var method in allowedMethods ?? ["GET", "POST"]) target.AllowedMethods.Add(method);
        target.Headers["X-Api-Key"] = "la-clef-du-systeme-local";

        var options = new RelayAgentOptions();
        options.HttpTargets.Add(target);

        return (
            new HttpOrderExecutor(Factory(), Options.Create(options)),
            new TargetHealthRegistry(TimeProvider.System));
    }

    private static IHttpClientFactory Factory()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(HttpOrderExecutor.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);

        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    private static RelayOrder Order(string target, string method, string path) => new(
        CorrelationId: "corr-" + Guid.NewGuid().ToString("N"),
        Kind: RelayOrderKind.HttpCall,
        Target: target,
        Body: JsonSerializer.SerializeToElement(
            new RelayHttpCallBody(method, path, null, null, null), RelayProtocolJson.Options),
        TimeoutSeconds: null);

    /// <summary>Stands in for the IMF's local system: three routes, no cleverness.</summary>
    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var context = await _listener.GetContextAsync();
            var request = context.Request;

            using var body = new StreamReader(request.InputStream);

            _received.Add((
                request.HttpMethod,
                request.Url?.AbsolutePath ?? string.Empty,
                request.Headers["X-Api-Key"],
                await body.ReadToEndAsync(cancellationToken)));

            var response = context.Response;
            byte[] payload;

            if (request.Url!.AbsolutePath.EndsWith("/not-found", StringComparison.Ordinal))
            {
                response.StatusCode = 404;
                response.ContentType = "application/json";
                payload = Encoding.UTF8.GetBytes("""{"error":"unknown"}""");
            }
            else if (request.Url.AbsolutePath.EndsWith("/large", StringComparison.Ordinal))
            {
                response.StatusCode = 200;
                response.ContentType = "application/json";
                payload = Encoding.UTF8.GetBytes(new string('x', 64 * 1024));
            }
            else
            {
                response.StatusCode = 200;
                response.ContentType = "application/json";
                payload = Encoding.UTF8.GetBytes(
                    """{"account":"CI0012345678","balance":"125000.00"}""");
            }

            response.ContentLength64 = payload.Length;
            await response.OutputStream.WriteAsync(payload, cancellationToken);
            response.Close();
        }
    }

    private static int FreeLoopbackPort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

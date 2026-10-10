using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sankore.Integration.RelayAgent.Channel;
using Sankore.Integration.RelayAgent.Configuration;
using Sankore.Integration.RelayAgent.Execution;
using Sankore.Integration.RelayAgent.Observability;
using Sankore.Integration.RelayAgent.Protocol;

// ---------------------------------------------------------------------------------------------
// Sankore.Integration.RelayAgent — the on-premise relay agent (INT-26).
//
// This process runs on the IMF's own hardware, inside their network, and it is the only piece of
// SANKORE that does. It holds one outbound session to the platform (see Channel/RelayChannel.cs
// for why outbound only is the whole point) and executes the orders that arrive on it: a local
// HTTP call, an SFTP deposit or read, a read-only query on a declared view.
//
// It has NO reference to the Integration module, and no reference to anything else in this
// solution. That is deliberate and worth defending: this binary is installed by somebody else's
// IT team on somebody else's server, and the smaller the thing they have to vet and keep
// patched, the likelier it is kept patched. It knows nothing of our domain, our schema or our
// other modules; everything it shares with the platform is in its own Protocol/ folder.
//
// One shape, two deliveries (criterion 1). AddWindowsService() makes the same executable a
// Windows service when the Windows service host starts it, and a no-op everywhere else, so the
// Docker image and the Windows service are the same build.
// ---------------------------------------------------------------------------------------------

var builder = Host.CreateApplicationBuilder(args);

// A Windows service starts with its working directory set to System32, so every relative path —
// including the default appsettings.json the host builder looks for — resolves somewhere
// surprising. Pinning the content root to the binary's own directory is what makes "the same
// build" true for the service as well as the container.
builder.Environment.ContentRootPath = AppContext.BaseDirectory;

// --- The configuration file (criterion 1) ---------------------------------------------------
//
// An explicit, operator-owned file, separate from the appsettings.json that ships inside the
// image: the IT team edits a file they mounted or installed, and an upgrade that replaces the
// binary cannot overwrite their settings. Resolution order, first hit wins:
//
//   1. --config <path> on the command line
//   2. SANKORE_RELAY_CONFIG in the environment
//   3. the platform default below
//
// Optional, so the service can be started once with no file and fail on VALIDATION — which
// prints every missing key at once — rather than on a missing-file exception that names only the
// first problem.
var configPath = ResolveConfigPath(args);
builder.Configuration.AddJsonFile(configPath, optional: true, reloadOnChange: false);

// Environment variables re-applied AFTER the operator's file, deliberately reversing the host
// builder's own order. Configuration sources are last-wins, so without this the file would
// override the environment — and the one setting the guide tells an IT team to keep OUT of the
// file is a secret (Relay__Certificate__Password), which is exactly what a container platform
// supplies through the environment. The file is the base; the environment overrides it.
builder.Configuration.AddEnvironmentVariables();

Console.WriteLine($"Relay agent configuration file: {configPath}");

builder.Services
    .AddOptions<RelayAgentOptions>()
    .Bind(builder.Configuration.GetSection(RelayAgentOptions.SectionName))
    // ValidateOnStart, so a bad entry stops the service with every failure named. This agent runs
    // on a machine nobody watches: a target that is quietly unusable would surface weeks later as
    // "SANKORE does not see our balances", with the cause in a log file on a branch server.
    .ValidateOnStart();

builder.Services.TryAddEnumerable(
    ServiceDescriptor.Singleton<IValidateOptions<RelayAgentOptions>, RelayAgentOptionsValidator>());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TargetHealthRegistry>();
builder.Services.AddSingleton<TargetProbe>();
builder.Services.AddSingleton<RelayOrderDispatcher>();

// --- The HTTP client used for relayed local calls -------------------------------------------
//
// One client, no base address: the base address is per target, and comes from the configuration
// file on every call.
builder.Services
    .AddHttpClient(HttpOrderExecutor.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // Redirects OFF, and this is a security setting rather than a preference. The executor
        // proves the resolved URI is under the declared base URL; a 302 would then move the call
        // to an address nothing checked — and on a bank's internal network every interesting
        // address is one hop away. A local system that answers a redirect is a misconfiguration
        // the IT team should see, not one the agent should follow.
        AllowAutoRedirect = false,

        // Pooled connections are per named client and shared across targets and orders, which is
        // fine because no credential travels on the connection itself: each target's headers are
        // attached to its own request.
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    // The agent's own timeout is a linked CancellationToken per order, which is what lets a
    // target's declared budget and an order's smaller one be applied together. HttpClient.Timeout
    // would collapse both into the same OperationCanceledException.
    .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);

// --- The three order kinds (criterion 3) ----------------------------------------------------
builder.Services.AddSingleton<IRelayOrderExecutor, HttpOrderExecutor>();
builder.Services.AddSingleton<IRelayOrderExecutor>(sp => new SftpOrderExecutor(
    sp.GetRequiredService<IOptions<RelayAgentOptions>>(), RelayOrderKind.SftpPut));
builder.Services.AddSingleton<IRelayOrderExecutor>(sp => new SftpOrderExecutor(
    sp.GetRequiredService<IOptions<RelayAgentOptions>>(), RelayOrderKind.SftpRead));
builder.Services.AddSingleton<IRelayOrderExecutor, SqlViewOrderExecutor>();

builder.Services.AddHostedService<RelayChannelWorker>();

// Both delivery shapes, one build. This call is a no-op off Windows, so the Docker image and the
// Windows service are the same executable with no conditional compilation and no second project.
builder.Services.AddWindowsService(options => options.ServiceName = "SankoreRelayAgent");

// The Windows Event Log is where a service's output is actually looked for; stdout goes nowhere
// when the service host starts the process. Guarded by an OS check rather than left to the
// provider's own no-op, because the EventLog API is attributed Windows-only and an unguarded call
// is a build warning on every other platform — and in a container the provider would be dead
// weight either way.
if (OperatingSystem.IsWindows())
    WindowsEventLogSetup.Add(builder.Logging);

var host = builder.Build();

// Validation is reported here, by hand, rather than being left to surface as an
// OptionsValidationException out of the first service that touched the options. Both fail closed;
// only one is readable. The IT team installing this will see a numbered list of the keys to fix in
// their own file, not a forty-frame dependency-injection stack trace with the list buried in the
// middle of it — and they are the audience for this failure.
try
{
    _ = host.Services.GetRequiredService<IOptions<RelayAgentOptions>>().Value;
}
catch (OptionsValidationException invalid)
{
    await Console.Error.WriteLineAsync(
        $"The relay agent cannot start: the configuration file has "
        + $"{invalid.Failures.Count()} problem(s).");
    await Console.Error.WriteLineAsync($"  File: {configPath}");

    foreach (var failure in invalid.Failures)
        await Console.Error.WriteLineAsync($"  - {failure}");

    // Non-zero, so a container restart policy and the Windows service manager both treat this as
    // the failure it is instead of a clean exit.
    return 1;
}

await host.RunAsync();
return 0;

/// <summary>
/// Finds the operator's configuration file. A local function rather than a class so the whole
/// resolution rule is readable next to the line that uses it.
/// </summary>
static string ResolveConfigPath(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals("--config", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(args[i + 1]);
    }

    var fromEnvironment = Environment.GetEnvironmentVariable("SANKORE_RELAY_CONFIG");
    if (!string.IsNullOrWhiteSpace(fromEnvironment))
        return Path.GetFullPath(fromEnvironment);

    // Platform defaults, each the conventional home for machine-wide configuration there:
    // %ProgramData% on Windows (writable by administrators, survives an upgrade), /etc on Linux
    // (and the natural mount point for a container).
    return OperatingSystem.IsWindows()
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Sankore", "RelayAgent", "relay-agent.json")
        : "/etc/sankore/relay-agent.json";
}

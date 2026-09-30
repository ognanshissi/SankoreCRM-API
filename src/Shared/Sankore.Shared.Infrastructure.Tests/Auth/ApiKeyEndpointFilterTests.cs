namespace Sankore.Shared.Infrastructure.Tests.Auth;

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

/// <summary>
/// POST users/create-root provisions the system user of a tenant and cannot sit behind a JWT —
/// it is what creates the first account. It used to be reachable by anyone. These tests pin the
/// two rules that replaced that: a valid key is required, and a server that forgot to configure
/// one refuses the call instead of publishing it.
/// </summary>
public sealed class ApiKeyEndpointFilterTests
{
    private sealed class StubEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>Runs the filter and reports whether the endpoint itself was reached.</summary>
    private static async Task<(bool Reached, object? Result)> InvokeAsync(
        string? configuredKey, string? presentedKey, string environment = "Production")
    {
        var settings = new Dictionary<string, string?>();
        if (configuredKey is not null)
            settings[ApiKeyValidator.ConfigKey] = configuredKey;

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddSingleton<IHostEnvironment>(new StubEnvironment(environment));
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/api/v1/users/create-root";

        if (presentedKey is not null)
            httpContext.Request.Headers[ApiKeyValidator.HeaderName] = presentedKey;

        var reached = false;
        var context = new DefaultEndpointFilterInvocationContext(httpContext);

        var result = await new ApiKeyEndpointFilter().InvokeAsync(context, _ =>
        {
            reached = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        });

        return (reached, result);
    }

    [Fact]
    public async Task Lets_the_request_through_when_the_key_matches()
    {
        var (reached, _) = await InvokeAsync(configuredKey: "sk-live-abc", presentedKey: "sk-live-abc");

        reached.Should().BeTrue();
    }

    [Fact]
    public async Task Refuses_a_request_with_no_header()
    {
        var (reached, result) = await InvokeAsync(configuredKey: "sk-live-abc", presentedKey: null);

        reached.Should().BeFalse("the endpoint must not run");
        StatusOf(result).Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Refuses_a_request_with_the_wrong_key()
    {
        var (reached, result) = await InvokeAsync(configuredKey: "sk-live-abc", presentedKey: "sk-live-abd");

        reached.Should().BeFalse();
        StatusOf(result).Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Refuses_the_call_in_production_when_no_key_is_configured()
    {
        // The whole point: a deployment that forgot the key must not leave super-user
        // provisioning open. 503 rather than 401 — the fault is the server's, not the caller's.
        var (reached, result) = await InvokeAsync(configuredKey: null, presentedKey: "anything");

        reached.Should().BeFalse();
        StatusOf(result).Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Stays_open_in_development_when_no_key_is_configured()
    {
        // Local runs have no ApiKey in user-secrets; requiring one would break every dev setup.
        var (reached, _) = await InvokeAsync(
            configuredKey: null, presentedKey: null, environment: "Development");

        reached.Should().BeTrue();
    }

    [Theory]
    [InlineData("sk-live-abc", "sk-live-abc", true)]
    [InlineData("sk-live-abc", "sk-live-ab", false)]   // prefix only
    [InlineData("sk-live-abc", "SK-LIVE-ABC", false)]  // case matters
    [InlineData("sk-live-abc", "", false)]
    [InlineData("", "sk-live-abc", false)]
    [InlineData(null, null, false)]
    public void Matches_compares_the_whole_value(string? expected, string? provided, bool matches)
        => ApiKeyValidator.Matches(expected, provided).Should().Be(matches);

    private static int? StatusOf(object? result) =>
        result is IStatusCodeHttpResult status ? status.StatusCode : null;

    /// <summary>Minimal EndpointFilterInvocationContext — the framework's own is internal.</summary>
    private sealed class DefaultEndpointFilterInvocationContext(HttpContext httpContext)
        : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; } = httpContext;
        public override IList<object?> Arguments { get; } = [];
        public override T GetArgument<T>(int index) => throw new NotSupportedException();
    }
}

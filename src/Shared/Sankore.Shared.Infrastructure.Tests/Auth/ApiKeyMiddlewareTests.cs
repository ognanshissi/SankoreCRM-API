namespace Sankore.Shared.Infrastructure.Tests.Auth;

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

/// <summary>
/// What the host-wide API key gate lets through.
///
/// <para>
/// <b>Why this suite exists.</b> This middleware guards the whole of Sankore.Admin, which means
/// it also answered the container's own liveness probe — with 401. The registry stayed
/// `unhealthy` for ever, its logs filled with `API key missing on GET /health` warnings that
/// drowned real ones, and Compose refused to start Sankore.Api, which waited on that health:
/// `dependency failed to start: container sankore.admin is unhealthy`. A gate that cannot be
/// probed takes the host down with it.
/// </para>
///
/// <para>
/// So the probe paths pass, and nothing else does. Both halves are pinned here: an exemption
/// list is exactly the kind of thing that grows a prefix nobody meant to publish.
/// </para>
/// </summary>
public sealed class ApiKeyMiddlewareTests
{
    private const string ConfiguredKey = "configured-key";

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task Lets_a_liveness_probe_through_without_a_key(string path)
    {
        var (reached, status) = await InvokeAsync(path, presentedKey: null);

        reached.Should().BeTrue("a container health check cannot carry a secret");
        status.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Lets_a_probe_through_even_with_a_wrong_key()
    {
        // Nothing is gained by refusing it, and a probe that fails only sometimes is worse than
        // one that never does.
        var (reached, _) = await InvokeAsync("/health", presentedKey: "wrong");

        reached.Should().BeTrue();
    }

    [Theory]
    [InlineData("/api/v1/tenants")]
    [InlineData("/api/v1/tenants/by-fqdn")]
    // Not a probe: the exemption matches path SEGMENTS, so a route that merely starts with the
    // same letters stays closed.
    [InlineData("/healthy-tenants")]
    [InlineData("/")]
    public async Task Refuses_everything_else_without_a_key(string path)
    {
        var (reached, status) = await InvokeAsync(path, presentedKey: null);

        reached.Should().BeFalse();
        status.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Admits_a_real_route_with_the_right_key()
    {
        var (reached, status) = await InvokeAsync("/api/v1/tenants", presentedKey: ConfiguredKey);

        reached.Should().BeTrue();
        status.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Skips_validation_entirely_when_no_key_is_configured()
    {
        // Documented behaviour of THIS middleware, and the one place it differs from
        // ApiKeyEndpointFilter: it guards a host that is internal by construction, so an
        // unconfigured key is a development convenience rather than an open super-user endpoint.
        var (reached, _) = await InvokeAsync("/api/v1/tenants", presentedKey: null, configuredKey: null);

        reached.Should().BeTrue();
    }

    private static async Task<(bool Reached, int Status)> InvokeAsync(
        string path, string? presentedKey, string? configuredKey = ConfiguredKey)
    {
        var settings = new Dictionary<string, string?>();
        if (configuredKey is not null)
            settings[ApiKeyValidator.ConfigKey] = configuredKey;

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = path;
        httpContext.Response.Body = new MemoryStream();

        if (presentedKey is not null)
            httpContext.Request.Headers[ApiKeyValidator.HeaderName] = presentedKey;

        var reached = false;

        var middleware = new ApiKeyMiddleware(
            _ =>
            {
                reached = true;
                httpContext.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            config,
            NullLogger<ApiKeyMiddleware>.Instance);

        await middleware.InvokeAsync(httpContext);

        return (reached, httpContext.Response.StatusCode);
    }
}

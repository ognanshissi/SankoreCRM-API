namespace Sankore.Shared.Infrastructure.Tests.Auth;

using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

/// <summary>
/// The Swagger UI and the OpenAPI document are a complete map of the API and a ready-made client
/// for it. These tests pin the two rules that now guard them: valid Basic credentials are
/// required, and a host that exposes them without configuring a password serves nothing rather
/// than serving them openly.
/// </summary>
public sealed class SwaggerBasicAuthMiddlewareTests
{
    private sealed class StubEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>Runs the middleware and reports whether Swagger itself would have been reached.</summary>
    private static async Task<(bool Reached, HttpContext Context)> InvokeAsync(
        string path = "/swagger/index.html",
        string? configuredUser = "sankore",
        string? configuredPassword = "s3cret",
        string? authorizationHeader = null,
        string environment = "Production")
    {
        var settings = new Dictionary<string, string?>
        {
            [SwaggerBasicAuthMiddleware.UsernameConfigKey] = configuredUser,
            [SwaggerBasicAuthMiddleware.PasswordConfigKey] = configuredPassword,
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        if (authorizationHeader is not null)
            context.Request.Headers.Authorization = authorizationHeader;

        var reached = false;

        var middleware = new SwaggerBasicAuthMiddleware(
            _ => { reached = true; return Task.CompletedTask; },
            config,
            new StubEnvironment(environment),
            NullLogger<SwaggerBasicAuthMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        return (reached, context);
    }

    private static string Basic(string user, string password)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    [Fact]
    public async Task Serves_the_documentation_when_the_credentials_match()
    {
        var (reached, context) = await InvokeAsync(authorizationHeader: Basic("sankore", "s3cret"));

        reached.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Challenges_a_request_with_no_credentials()
    {
        var (reached, context) = await InvokeAsync();

        reached.Should().BeFalse("Swagger must not be served");
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        // Without this header the browser never shows a login prompt, so the UI looks broken.
        context.Response.Headers.WWWAuthenticate.ToString().Should().StartWith("Basic realm=");
    }

    [Theory]
    [InlineData("sankore", "wrong")]
    [InlineData("wrong", "s3cret")]
    [InlineData("SANKORE", "s3cret")]   // case matters on both halves
    [InlineData("sankore", "s3cre")]    // prefix only
    public async Task Challenges_a_request_with_wrong_credentials(string user, string password)
    {
        var (reached, context) = await InvokeAsync(authorizationHeader: Basic(user, password));

        reached.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Theory]
    [InlineData("Bearer eyJhbGciOi")]          // right shape, wrong scheme
    [InlineData("Basic not-base64!!")]
    [InlineData("Basic c2Fua29yZQ==")]         // base64 of "sankore" — no colon, so no password
    public async Task Challenges_a_malformed_authorization_header(string header)
    {
        var (reached, context) = await InvokeAsync(authorizationHeader: header);

        reached.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Refuses_outside_development_when_no_password_is_configured()
    {
        // The whole point: a host that publishes Swagger and forgot the password must serve no
        // documentation at all. 503 rather than 401 — the fault is the server's, not the caller's.
        var (reached, context) = await InvokeAsync(
            configuredPassword: null, authorizationHeader: Basic("sankore", "s3cret"));

        reached.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Stays_open_in_development_when_no_password_is_configured()
    {
        // A local `dotnet run` must not start asking for credentials — same rule as
        // ApiKeyEndpointFilter.
        var (reached, _) = await InvokeAsync(
            configuredPassword: null, environment: "Development");

        reached.Should().BeTrue();
    }

    [Fact]
    public async Task Requires_credentials_in_development_once_they_are_configured()
    {
        var (reached, context) = await InvokeAsync(environment: "Development");

        reached.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Theory]
    [InlineData("/swagger")]
    [InlineData("/swagger/v1/swagger.json")]
    [InlineData("/SWAGGER/index.html")]        // routing is case-insensitive, so the gate must be
    [InlineData("/openapi/v1.json")]
    public async Task Guards_every_documentation_path(string path)
    {
        var (reached, context) = await InvokeAsync(path);

        reached.Should().BeFalse($"{path} exposes the API map");
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Theory]
    [InlineData("/api/v1/leads")]
    [InlineData("/health")]
    [InlineData("/api/ingest/web/abc123")]
    [InlineData("/swaggerish")]                // prefix match must be on path segments
    public async Task Leaves_every_other_request_alone(string path)
    {
        var (reached, context) = await InvokeAsync(path);

        reached.Should().BeTrue($"{path} is not documentation");
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }
}

/// <summary>
/// <see cref="SwaggerExposure"/> decides whether the documentation is mapped at all. The default
/// must stay what the hosts did inline before it existed — Development only — because changing it
/// would publish the API map on every deployed host at once.
/// </summary>
public sealed class SwaggerExposureTests
{
    private sealed class StubEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static bool IsEnabled(string? configured, string environment)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SwaggerExposure.EnabledConfigKey] = configured,
            })
            .Build();

        return SwaggerExposure.IsEnabled(config, new StubEnvironment(environment));
    }

    [Theory]
    [InlineData(null, "Development", true)]
    [InlineData(null, "Production", false)]
    [InlineData(null, "Staging", false)]
    [InlineData("", "Production", false)]        // set but empty is still "not configured"
    [InlineData("true", "Production", true)]
    [InlineData("false", "Development", false)]  // and it turns the UI off locally too
    public void Defaults_to_development_and_obeys_the_override(
        string? configured, string environment, bool enabled)
        => IsEnabled(configured, environment).Should().Be(enabled);
}

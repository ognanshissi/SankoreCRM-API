namespace Sankore.Shared.Infrastructure.Tests.Tenants;

using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Models;
using Xunit;

/// <summary>
/// Which requests this middleware lets through without a caller identity.
///
/// <para>
/// <b>Why this suite exists.</b> The middleware runs BEFORE routing, so it answers for every
/// request, and its exempt list held four infrastructure paths. Every public surface of the
/// product sat outside it: the web-form ingest, the webhook ingest, the form definition the SDK
/// fetches, the SDK file itself, the integration sync hooks and the relay agent's enrolment. All
/// of them are tenantless by design — the tenant comes from a public key, a connection id or a
/// client certificate in the URL — so all of them answered
/// 400 "Cannot determine tenant: authenticate via JWT or call from a recognised domain" and
/// never reached their endpoint. Nothing failed at start-up and nothing was logged; the symptom
/// was a lead-capture form that silently never captured.
/// </para>
///
/// <para>
/// The exempt prefixes now come from the host, so this suite passes the same list
/// <c>Program.cs</c> does: a route added below <c>app.Map*PublicEndpoints()</c> without a
/// matching prefix fails here.
/// </para>
/// </summary>
public sealed class TenantResolutionMiddlewareTests
{
    /// <summary>The prefixes Sankore.Api passes to UseTenantResolution.</summary>
    private static readonly string[] HostExemptPrefixes =
        ["/api/ingest", "/sdk", "/integration/webhooks", "/integration/relay-agents"];

    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Theory]
    // M13 — the public key in the path IS the tenant reference.
    [InlineData("/api/ingest/web/pk_live_abc")]
    [InlineData("/api/ingest/web/pk_live_abc/form")]
    [InlineData("/api/ingest/web/pk_live_abc/ping")]
    [InlineData("/api/ingest/hooks/pk_live_abc")]
    // The SDK build the embedding page loads before it can call anything at all.
    [InlineData("/sdk/1.1.0/forms.min.js")]
    [InlineData("/sdk/v1/forms.min.js")]
    // M14 — connection id plus an HMAC over the body, and the relay agent's certificate.
    [InlineData("/integration/webhooks/8f14e45f-ceea-467a-9f4e-0f4e2e1e0001")]
    [InlineData("/integration/relay-agents/enrol")]
    [InlineData("/integration/relay-agents/heartbeat")]
    // Infrastructure paths every host has.
    [InlineData("/health")]
    [InlineData("/alive")]
    [InlineData("/swagger/index.html")]
    [InlineData("/api/v1/public/anything")]
    public async Task A_public_surface_reaches_its_endpoint_without_a_tenant(string path)
    {
        var ctx = Request(path);
        var reached = false;

        await Pipeline(_ => { reached = true; return Task.CompletedTask; })(ctx);

        reached.Should().BeTrue(
            "{0} resolves its own tenant from the URL and has no JWT or recognised domain", path);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Theory]
    [InlineData("/api/v1/leads")]
    [InlineData("/api/v1/integration/connections")]
    [InlineData("/api/ingestion-not-a-prefix-match")]
    public async Task Everything_else_still_needs_a_tenant(string path)
    {
        var ctx = Request(path);
        var reached = false;

        await Pipeline(_ => { reached = true; return Task.CompletedTask; })(ctx);

        // The exemption is a list of prefixes, not a hole: an authenticated route is unchanged,
        // and a path that merely starts with the same letters is not a segment match.
        reached.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task An_exempt_request_is_not_given_a_tenant_it_did_not_prove()
    {
        // Passing through must not look like a resolved tenant: the endpoint behind the prefix
        // establishes its own, and a value in Items here would hand it somebody else's.
        var ctx = Request("/api/ingest/hooks/pk_live_abc");

        await Pipeline(_ => Task.CompletedTask)(ctx);

        ctx.Items.ContainsKey(TenantKey.ResolvedTenantKey).Should().BeFalse();
    }

    [Fact]
    public async Task An_exempt_path_still_refuses_a_forged_tenant_header()
    {
        // The x-tenant-id rejection is the one guard that must survive the exemption: it is a
        // tenant-impersonation attempt whatever the path, and a public route is exactly where an
        // unauthenticated caller would try it.
        var ctx = Request("/api/ingest/hooks/pk_live_abc");
        ctx.Request.Headers["x-tenant-id"] = Guid.NewGuid().ToString();
        var reached = false;

        await Pipeline(_ => { reached = true; return Task.CompletedTask; })(ctx);

        reached.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task A_jwt_tenant_still_resolves_on_a_normal_route()
    {
        var ctx = Request("/api/v1/leads");
        ctx.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim("tenant_id", TenantId.ToString())], "test"));

        await Pipeline(_ => Task.CompletedTask)(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        ctx.Items[TenantKey.ResolvedTenantKey].Should().Be(TenantId);
    }

    // ── Harness ─────────────────────────────────────────────────────────

    private static DefaultHttpContext Request(string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    /// <summary>
    /// Built through <see cref="TenantStoreServiceCollectionExtensions.UseTenantResolution"/> and
    /// a real <see cref="ApplicationBuilder"/>, not with <c>new</c>.
    ///
    /// <para>
    /// Constructing the middleware directly is what let a start-up failure through: the exempt
    /// prefixes are a constructor argument passed by <c>UseMiddleware</c>, and
    /// <c>params object?[] args</c> took the covariant <c>string[]</c> as the argument LIST
    /// rather than as one argument — "A suitable constructor could not be located", at boot,
    /// while these tests stayed green. Going through the pipeline exercises the activation too.
    /// </para>
    /// </summary>
    private static RequestDelegate Pipeline(RequestDelegate terminal)
    {
        var store = Substitute.For<ITenantStore>();
        store.GetAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantInfo(TenantId, "IMF", "imf.ci", true, false, null, null));

        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddLogging();

        var app = new ApplicationBuilder(services.BuildServiceProvider());
        app.UseTenantResolution(HostExemptPrefixes);
        app.Run(terminal);

        return app.Build();
    }
}

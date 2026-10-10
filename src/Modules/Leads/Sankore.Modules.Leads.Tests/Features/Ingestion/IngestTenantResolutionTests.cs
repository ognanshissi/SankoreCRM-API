namespace Sankore.Modules.Leads.Tests.Features.Ingestion;

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Ingestion;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel.Models;
using Xunit;

/// <summary>
/// How a public ingest request acquires the tenant it is exempt from proving.
///
/// <para>
/// <b>Why this suite exists.</b> These routes are exempt from <c>TenantResolutionMiddleware</c>
/// by design — a webhook caller has no JWT and no recognised domain. But the commands they send
/// implement <c>ICommand</c>, so <c>AuditBehavior</c> reads <c>ITenantContext.CurrentTenantId</c>
/// and threw <c>InvalidOperationException: No tenant resolved for the current context</c> — on
/// the audit write, AFTER the lead had been ingested, so the caller saw a 500 for work that had
/// actually happened. The public key names exactly one source and therefore one tenant, so the
/// request can be scoped; it just has to be scoped before the pipeline asks.
/// </para>
/// </summary>
public sealed class IngestTenantResolutionTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public IngestTenantResolutionTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_known_public_key_scopes_the_request_to_its_source_tenant()
    {
        var source = await SeedSource("pk_live_abc");
        var http = Request("pk_live_abc");
        var reached = false;

        var result = await IngestTenantResolution.ResolveAsync(
            http, () => { reached = true; return ValueTask.FromResult<object?>(null); });

        reached.Should().BeTrue();
        result.Should().BeNull();

        // The same slot the middleware publishes to, so audit, the query filters and every
        // nested command behave as they do on an authenticated request.
        http.Items[TenantKey.ResolvedTenantKey].Should().Be(source.TenantId);
    }

    [Fact]
    public async Task An_unknown_public_key_is_a_404_and_never_reaches_the_pipeline()
    {
        await SeedSource("pk_live_abc");
        var http = Request("pk_live_nope");
        var reached = false;

        var result = await IngestTenantResolution.ResolveAsync(
            http, () => { reached = true; return ValueTask.FromResult<object?>(null); });

        // 404 is what all five endpoints already answer for an unknown key, so the behaviour is
        // unchanged — and short-circuiting matters: letting it through would reach an audit write
        // with no tenant and turn a 404 into a 500.
        reached.Should().BeFalse();
        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.NotFound>();
        http.Items.ContainsKey(TenantKey.ResolvedTenantKey).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_missing_public_key_is_a_404_rather_than_an_unscoped_request(string? publicKey)
    {
        var http = Request(publicKey);
        var reached = false;

        var result = await IngestTenantResolution.ResolveAsync(
            http, () => { reached = true; return ValueTask.FromResult<object?>(null); });

        reached.Should().BeFalse();
        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.NotFound>();
    }

    [Fact]
    public async Task A_source_in_any_status_still_scopes_the_request()
    {
        // The filter resolves the TENANT, not the right to ingest: Draft, Paused and Archived
        // all belong to a tenant, and each endpoint answers for the status itself (404 for
        // archived, 403 for paused). Filtering on status here would flatten those into one 404
        // and lose the distinction the SDK relies on to decide whether to retry.
        var source = await SeedSource("pk_live_archived", s => s.Archive());

        var http = Request("pk_live_archived");
        await IngestTenantResolution.ResolveAsync(http, () => ValueTask.FromResult<object?>(null));

        http.Items[TenantKey.ResolvedTenantKey].Should().Be(source.TenantId);
    }

    [Fact]
    public async Task The_tenant_comes_from_the_source_and_not_from_the_caller()
    {
        // The guarantee that matters: a webhook caller supplies only a public key, so it cannot
        // name a tenant. Two sources in different tenants resolve to their own.
        var mine = await SeedSource("pk_mine");
        var other = Guid.NewGuid();
        await using (var db = _factory.CreateContext())
        {
            db.LeadSourceConfigs.Add(NewSource(other, "pk_other"));
            await db.SaveChangesAsync();
        }

        var first = Request("pk_mine");
        await IngestTenantResolution.ResolveAsync(first, () => ValueTask.FromResult<object?>(null));
        first.Items[TenantKey.ResolvedTenantKey].Should().Be(mine.TenantId);

        var second = Request("pk_other");
        await IngestTenantResolution.ResolveAsync(second, () => ValueTask.FromResult<object?>(null));
        second.Items[TenantKey.ResolvedTenantKey].Should().Be(other);
    }

    // ── Harness ─────────────────────────────────────────────────────────

    private static LeadSourceConfig NewSource(Guid tenantId, string publicKey)
    {
        var source = LeadSourceConfig.Create(
            tenantId, publicKey.ToUpperInvariant(), "Hook",
            LeadChannelType.InboundWebhook, 0,
            IntegrationMode.ServerWebhook,
            settings: new ServerWebhookSettings());

        // PublicKey is generated by the aggregate for push modes; pin it so the lookup is exact.
        typeof(LeadSourceConfig)
            .GetProperty(nameof(LeadSourceConfig.PublicKey))!
            .SetValue(source, publicKey);

        return source;
    }

    private async Task<LeadSourceConfig> SeedSource(
        string publicKey, Action<LeadSourceConfig>? arrange = null)
    {
        await using var db = _factory.CreateContext();
        var source = NewSource(_tenantId, publicKey);
        arrange?.Invoke(source);
        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync();
        return source;
    }

    private DefaultHttpContext Request(string? publicKey)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _factory.CreateContext());

        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (publicKey is not null) http.Request.RouteValues["publicKey"] = publicKey;
        return http;
    }
}

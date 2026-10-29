namespace Sankore.Modules.Integration.Tests.Features.Webhooks;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Webhooks;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Tests.Features.Commands;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Egress;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Outbound webhooks, lot 2: an administrator registers a partner's address and the events it
/// wants, receives a signing secret once, can rotate it, switch the subscription off and send a
/// test. Delivering real events is lot 3.
/// </summary>
public sealed class WebhookSubscriptionsTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid OtherTenant = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private readonly TestIntegrationDbContextFactory _factory = new(TenantId);
    private readonly InMemorySecrets _vault = new();
    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero));

    // ── Creation and the secret ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Creating_returns_the_secret_once_and_the_listing_only_shows_it_masked()
    {
        await using var db = _factory.CreateContext();

        var created = await Save(db, null, events: ["lead.created", "lead.status_changed"]);

        created.IsSuccess.Should().BeTrue();
        var secret = created.Value!.Secret;
        secret.Should().StartWith("whsec_").And.HaveLength("whsec_".Length + 43);

        var listed = await new ListWebhookSubscriptionsHandler(db, Mapper())
            .Handle(new ListWebhookSubscriptionsQuery(), CancellationToken.None);

        var row = listed.Should().ContainSingle().Subject;
        row.Events.Should().Equal("lead.created", "lead.status_changed");
        row.IsActive.Should().BeTrue();
        row.SecretHint.Should().NotBeNullOrEmpty().And.NotBe(secret);
        JsonSerializer.Serialize(listed).Should().NotContain(secret);
    }

    [Fact]
    public async Task Updating_changes_the_address_and_events_and_keeps_the_secret()
    {
        await using var db = _factory.CreateContext();
        var created = (await Save(db, null)).Value!;

        var updated = await Save(db, created.Subscription.Id, url: "https://partner.example.com/v2", events: ["client.created"]);

        updated.IsSuccess.Should().BeTrue();
        updated.Value!.Secret.Should().BeEmpty("an update never hands a secret out");
        updated.Value.Subscription.Url.Should().Be("https://partner.example.com/v2");
        updated.Value.Subscription.Events.Should().Equal("client.created");
        (await Secrets().SigningKeysAsync(TenantId, created.Subscription.Id, default)).Should().Equal(created.Secret);
    }

    [Fact]
    public async Task A_tenant_holds_twenty_subscriptions_at_most()
    {
        await using var db = _factory.CreateContext();
        for (var i = 0; i < SaveWebhookSubscriptionHandler.MaxPerTenant; i++)
            (await Save(db, null, name: $"Partenaire {i}")).IsSuccess.Should().BeTrue();

        (await Save(db, null)).Error.Should().Be(WebhookErrors.TooMany);
    }

    [Fact]
    public async Task Another_tenants_subscription_reads_as_not_found()
    {
        Guid foreignId;
        await using (var foreign = _factory.ContextFor(OtherTenant))
        {
            var subscription = WebhookSubscription.Create(OtherTenant, "Autre", "https://other.example.com/", null, ["lead.created"], Guid.NewGuid(), _clock.GetUtcNow()).Value!;
            foreign.WebhookSubscriptions.Add(subscription);
            await foreign.SaveChangesAsync();
            foreignId = subscription.Id;
        }

        await using var db = _factory.CreateContext();
        var user = CommandsTestHarness.User(TenantId);

        (await new GetWebhookSubscriptionHandler(db, Mapper()).Handle(new GetWebhookSubscriptionQuery(foreignId), default))
            .Error.Should().Be(WebhookErrors.NotFound);
        (await new DeleteWebhookSubscriptionHandler(db, user, Secrets()).Handle(new DeleteWebhookSubscriptionCommand(foreignId), default))
            .Error.Should().Be(WebhookErrors.NotFound);
        (await new RotateWebhookSecretHandler(db, user, Secrets()).Handle(new RotateWebhookSecretCommand(foreignId), default))
            .Error.Should().Be(WebhookErrors.NotFound);
    }

    // ── Validation ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://partner.example.com/hook", "lead.created")]
    [InlineData("https://10.0.0.5/hook", "lead.created")]
    [InlineData("https://localhost/hook", "lead.created")]
    [InlineData("https://partner.example.com:6379/hook", "lead.created")]
    [InlineData("https://partner.example.com/hook", "lead.exploded")]
    public async Task An_unusable_address_or_an_unknown_event_is_refused_on_the_field(string url, string code)
    {
        var validator = new SaveWebhookSubscriptionValidator(Options.Create(new OutboundHttpOptions()));

        var result = await validator.ValidateAsync(new SaveWebhookSubscriptionCommand(null, "Partenaire", url, null, [code]));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().OnlyContain(e => !string.IsNullOrWhiteSpace(e.ErrorMessage));
    }

    [Fact]
    public async Task No_event_at_all_is_refused()
    {
        var validator = new SaveWebhookSubscriptionValidator(Options.Create(new OutboundHttpOptions()));

        (await validator.ValidateAsync(new SaveWebhookSubscriptionCommand(null, "Partenaire", "https://partner.example.com/", null, [])))
            .IsValid.Should().BeFalse();
    }

    // ── Rotation, activation, deletion ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_rotated_secret_signs_beside_the_previous_one_for_seven_days()
    {
        await using var db = _factory.CreateContext();
        var created = (await Save(db, null)).Value!;
        var id = created.Subscription.Id;

        var rotated = await new RotateWebhookSecretHandler(db, CommandsTestHarness.User(TenantId), Secrets())
            .Handle(new RotateWebhookSecretCommand(id), default);

        rotated.Value!.Secret.Should().NotBe(created.Secret);
        rotated.Value.PreviousValidUntil.Should().Be(_clock.GetUtcNow().AddDays(7));
        (await Secrets().SigningKeysAsync(TenantId, id, default)).Should().Equal(rotated.Value.Secret, created.Secret);

        _clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        (await Secrets().SigningKeysAsync(TenantId, id, default)).Should().Equal(rotated.Value.Secret);
    }

    [Fact]
    public void Switching_on_by_hand_clears_the_failure_streak()
    {
        var subscription = WebhookSubscription.Create(TenantId, "P", "https://p.example.com/", null, ["lead.created"], Guid.NewGuid(), _clock.GetUtcNow()).Value!;
        typeof(WebhookSubscription).GetProperty(nameof(WebhookSubscription.ConsecutiveFailures))!.SetValue(subscription, 20);
        typeof(WebhookSubscription).GetProperty(nameof(WebhookSubscription.DisabledAt))!.SetValue(subscription, _clock.GetUtcNow());
        subscription.SetActive(false, Guid.NewGuid(), _clock.GetUtcNow());

        subscription.SetActive(true, Guid.NewGuid(), _clock.GetUtcNow());

        subscription.ConsecutiveFailures.Should().Be(0);
        subscription.DisabledAt.Should().BeNull();
        subscription.Wants("lead.created").Should().BeTrue();
        subscription.Wants("lead.closed").Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_removes_the_subscription_and_its_secrets()
    {
        await using var db = _factory.CreateContext();
        var created = (await Save(db, null)).Value!;

        var deleted = await new DeleteWebhookSubscriptionHandler(db, CommandsTestHarness.User(TenantId), Secrets())
            .Handle(new DeleteWebhookSubscriptionCommand(created.Subscription.Id), default);

        deleted.IsSuccess.Should().BeTrue();
        (await db.WebhookSubscriptions.CountAsync()).Should().Be(0);
        (await Secrets().SigningKeysAsync(TenantId, created.Subscription.Id, default)).Should().BeEmpty();
    }

    // ── What a partner receives ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_test_sends_a_signed_sample_the_partner_can_verify()
    {
        await using var db = _factory.CreateContext();
        var created = (await Save(db, null, events: ["lead.status_changed"])).Value!;
        var partner = new RecordingPartner(HttpStatusCode.OK);

        var result = await new TestWebhookSubscriptionHandler(db, CommandsTestHarness.User(TenantId), Secrets(), Sender(partner))
            .Handle(new TestWebhookSubscriptionCommand(created.Subscription.Id, null), default);

        result.Value!.Succeeded.Should().BeTrue();
        result.Value.EventCode.Should().Be("lead.status_changed");

        var request = partner.Requests.Should().ContainSingle().Subject;
        request.Headers[WebhookPayload.EventHeader].Should().Be("lead.status_changed");

        using var body = JsonDocument.Parse(request.Body);
        body.RootElement.GetProperty("type").GetString().Should().Be("lead.status_changed");
        body.RootElement.GetProperty("test").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("data").GetProperty("status").GetString().Should().Be("Open");
        request.Headers[WebhookPayload.DeliveryHeader].Should().Be(body.RootElement.GetProperty("id").GetString());

        // What the partner does: recompute HMAC-SHA256("{t}.{body}") with the secret it was given.
        var parts = request.Headers[WebhookPayload.SignatureHeader].Split(',');
        var t = parts.Single(p => p.StartsWith("t="))[2..];
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(created.Secret), Encoding.UTF8.GetBytes($"{t}.{request.Body}")));
        parts.Should().Contain($"v1={expected}");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP_500")]
    [InlineData(HttpStatusCode.Gone, "HTTP_410")]
    public async Task A_refusal_is_reported_by_its_status(HttpStatusCode status, string code)
    {
        await using var db = _factory.CreateContext();
        var created = (await Save(db, null)).Value!;

        var result = await new TestWebhookSubscriptionHandler(db, CommandsTestHarness.User(TenantId), Secrets(), Sender(new RecordingPartner(status)))
            .Handle(new TestWebhookSubscriptionCommand(created.Subscription.Id, "lead.created"), default);

        result.Value!.Succeeded.Should().BeFalse();
        result.Value.StatusCode.Should().Be((int)status);
        result.Value.FailureCode.Should().Be(code);
    }

    [Fact]
    public async Task The_event_types_are_listed_once_per_code_with_a_sample()
    {
        var types = await new ListWebhookEventTypesHandler(CommandsTestHarness.User(TenantId), FixedTenantModules.All)
            .Handle(new ListWebhookEventTypesQuery(), default);

        types.Select(t => t.Code).Should().Equal(WebhookEventCatalog.Codes);
        types.Should().OnlyContain(t => t.Available);
        types.Single(t => t.Code == "client.updated").SamplePayload.Should().Contain("\"type\":\"client.updated\"");
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────

    private Task<Result<CreatedWebhookSubscriptionDto>> Save(
        IntegrationDbContext db, Guid? id, string name = "Partenaire", string url = "https://partner.example.com/hooks",
        IReadOnlyList<string>? events = null)
        => new SaveWebhookSubscriptionHandler(db, CommandsTestHarness.User(TenantId), FixedTenantModules.All, Secrets(), Mapper(), _clock)
            .Handle(new SaveWebhookSubscriptionCommand(id, name, url, null, events ?? ["lead.created"]), CancellationToken.None);

    private WebhookSecrets Secrets() => new(_vault, _clock);

    private WebhookSubscriptionMapper Mapper() => new(_vault, _clock);

    private WebhookSender Sender(RecordingPartner partner)
        => new(new SingleClientFactory(new HttpClient(partner)), _clock, NullLogger<WebhookSender>.Instance);

    public void Dispose() => _factory.Dispose();
}

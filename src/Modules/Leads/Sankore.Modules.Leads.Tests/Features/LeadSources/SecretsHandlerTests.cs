namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.DeactivateLeadSource;
using Sankore.Modules.Leads.Features.LeadSources.Events;
using Sankore.Modules.Leads.Features.LeadSources.RotateHmacSecret;
using Sankore.Modules.Leads.Features.LeadSources.RotatePublicKey;
using Sankore.Modules.Leads.Features.LeadSources.SetSecret;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class SecretsHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly ISecretsModule _secrets;
    private readonly IEventPublisher _publisher;
    private readonly ITenantContext _tenant;

    public SecretsHandlerTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _secrets = Substitute.For<ISecretsModule>();
        _publisher = Substitute.For<IEventPublisher>();
        _tenant = new FixedTenantContext(_tenantId);
    }

    public void Dispose() => _factory.Dispose();

    private async Task<LeadSourceConfig> SeedSource()
    {
        await using var db = _factory.CreateContext();
        var source = LeadSourceConfig.Create(
            _tenantId, "HOOK", "Webhook",
            LeadChannelType.InboundWebhook, 0, IntegrationMode.ServerWebhook);
        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync();
        return source;
    }

    // ── SetSecret ────────────────────────────────────────────────────────

    [Fact]
    public async Task SetSecret_stores_and_returns_hint()
    {
        var source = await SeedSource();
        var expectedHint = new SecretHint("hmac-signing", "myse****cret", null);
        _secrets.GetHintAsync(Arg.Any<SecretKey>(), Arg.Any<CancellationToken>())
            .Returns(expectedHint);

        await using var db = _factory.CreateContext();
        var handler = new SetSecretHandler(db, _secrets, _tenant);
        var result = await handler.Handle(
            new SetSecretCommand(source.Id, "hmac-signing", "mysupersecret"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("hmac-signing");
        result.Value.MaskedValue.Should().Contain("****");

        await _secrets.Received(1).SetAsync(
            Arg.Is<SecretKey>(k => k.TenantId == _tenantId
                                   && k.Scope == "LeadSource"
                                   && k.EntityId == source.Id
                                   && k.Name == "hmac-signing"),
            "mysupersecret",
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetSecret_rejects_unknown_source()
    {
        await using var db = _factory.CreateContext();
        var handler = new SetSecretHandler(db, _secrets, _tenant);
        var result = await handler.Handle(
            new SetSecretCommand(Guid.NewGuid(), "hmac", "value"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("LEAD_SOURCE_NOT_FOUND");
    }

    // ── RotateHmac ──────────────────────────────────────────────────────

    [Fact]
    public async Task RotateHmac_generates_new_secret_and_keeps_old_7_days()
    {
        var source = await SeedSource();
        _secrets.GetValueAsync(Arg.Any<SecretKey>(), Arg.Any<CancellationToken>())
            .Returns("old-secret-value");

        await using var db = _factory.CreateContext();
        var handler = new RotateHmacSecretHandler(db, _secrets, _tenant);
        var result = await handler.Handle(
            new RotateHmacSecretCommand(source.Id),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Secret.Should().NotBeEmpty();
        result.Value.OldExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(7), TimeSpan.FromMinutes(1));

        // Old secret stored with expiry
        await _secrets.Received(1).SetAsync(
            Arg.Is<SecretKey>(k => k.TenantId == _tenantId && k.Name == "hmac-signing-old"),
            "old-secret-value",
            Arg.Is<DateTimeOffset?>(d => d.HasValue),
            Arg.Any<CancellationToken>());

        // New secret stored without expiry
        await _secrets.Received(1).SetAsync(
            Arg.Is<SecretKey>(k => k.TenantId == _tenantId && k.Name == "hmac-signing"),
            Arg.Any<string>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    // ── RotatePublicKey ─────────────────────────────────────────────────

    [Fact]
    public async Task RotatePublicKey_updates_and_publishes_event()
    {
        var source = await SeedSource();
        var oldKey = source.PublicKey;

        await using var db = _factory.CreateContext();
        var handler = new RotatePublicKeyHandler(db, _publisher);
        var result = await handler.Handle(
            new RotatePublicKeyCommand(source.Id),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(oldKey);
        result.Value.Should().NotContain("+").And.NotContain("/");

        await _publisher.Received(1).PublishAsync(
            Arg.Is<LeadSourcePublicKeyRotatedEvent>(e =>
                e.SourceId == source.Id && e.NewKeyHint.Contains("****")),
            Arg.Any<CancellationToken>());
    }

    // ── Archive deletes secrets ─────────────────────────────────────────

    [Fact]
    public async Task Archive_deletes_all_secrets()
    {
        var source = await SeedSource();

        await using var db = _factory.CreateContext();
        var handler = new DeactivateLeadSourceHandler(db, _secrets);
        var result = await handler.Handle(
            new DeactivateLeadSourceCommand(source.Id),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await _secrets.Received(1).DeleteAllAsync(_tenantId, "LeadSource", source.Id, Arg.Any<CancellationToken>());
    }
}

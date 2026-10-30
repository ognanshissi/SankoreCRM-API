namespace Sankore.Modules.Integration.Tests.Features.Webhooks;

using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Webhooks;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.TestMode;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Sandbox lot 4, « Copier en production » for an outbound webhook: matched by name, replayed
/// through the save command. A new production subscription is created switched off with its own
/// live secret, returned once; an existing one keeps its secret and its state.
/// </summary>
public sealed class PromoteWebhookSubscriptionTests : IDisposable
{
    private readonly Guid _live = Guid.NewGuid();
    private readonly Guid _twin = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly InMemorySecrets _vault = new();
    private readonly ServiceProvider _services;

    public PromoteWebhookSubscriptionTests()
    {
        var me = Substitute.For<ICurrentUser>();
        me.Id.Returns(Guid.NewGuid());
        me.TenantId.Returns(_twin);
        me.TeamTenantId.Returns(_live);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(IntegrationDbContext).Assembly));
        services.AddScoped<ICurrentUser>(_ => BackgroundJobContext.CurrentUser ?? me);
        services.AddScoped<ITenantContext>(_ => BackgroundJobContext.CurrentTenant ?? new FixedTenantContext(_twin));
        services.AddScoped(sp => new IntegrationDbContext(
            new DbContextOptionsBuilder<IntegrationDbContext>().UseInMemoryDatabase(_database).Options,
            sp.GetRequiredService<ITenantContext>()));
        services.AddSingleton<ISecretsModule>(_vault);
        services.AddSingleton<ITenantModules>(FixedTenantModules.All);
        services.AddScoped<WebhookSecrets>();
        services.AddScoped<WebhookSubscriptionMapper>();
        services.AddScoped<ProductionReplay>();
        services.AddScoped<WebhookSubscriptionCopier>();
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    private async Task<WebhookSubscription> SeedAsync(Guid tenantId, string url)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var subscription = WebhookSubscription.Create(tenantId, "ERP", url, null, ["lead.created"], Guid.NewGuid(), DateTimeOffset.UtcNow).Value!;
        db.WebhookSubscriptions.Add(subscription);
        await db.SaveChangesAsync();
        return subscription;
    }

    private async Task<Result<PromotionResult>> PromoteAsync(Guid id)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PromoteWebhookSubscriptionCommand(id, false));
    }

    private async Task<WebhookSubscription> LiveAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().WebhookSubscriptions
            .IgnoreQueryFilters().SingleAsync(s => s.TenantId == _live);
    }

    [Fact]
    public async Task A_new_production_webhook_is_switched_off_and_its_live_secret_shown_once()
    {
        var test = await SeedAsync(_twin, "https://erp-recette.example.com/hooks");

        var result = await PromoteAsync(test.Id);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Secret.Should().StartWith(TestModeKeys.LiveWebhookSecret).And.NotStartWith(TestModeKeys.TestWebhookSecret);
        var live = await LiveAsync();
        live.IsActive.Should().BeFalse();
        live.Url.Should().Be("https://erp-recette.example.com/hooks");
    }

    [Fact]
    public async Task An_existing_production_webhook_takes_the_address_and_keeps_its_state()
    {
        var existing = await SeedAsync(_live, "https://erp.example.com/old");
        var test = await SeedAsync(_twin, "https://erp.example.com/hooks/v2");

        var result = await PromoteAsync(test.Id);

        result.Value.Action.Should().Be(PromotionActions.Updated);
        result.Value.Secret.Should().BeNull();
        var live = await LiveAsync();
        live.Id.Should().Be(existing.Id);
        live.Url.Should().Be("https://erp.example.com/hooks/v2");
        live.IsActive.Should().BeTrue();
    }
}

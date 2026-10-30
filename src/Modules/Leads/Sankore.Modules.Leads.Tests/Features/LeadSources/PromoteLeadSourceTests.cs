namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Infrastructure.TestMode;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Sandbox lot 4, « Copier en production » for a lead source: matched by code, replayed through
/// the ordinary commands — a new production source is a draft with its own live key, an existing
/// one keeps its key, status and secrets, and no vault reference of the test mode ever crosses.
/// </summary>
public sealed class PromoteLeadSourceTests : IDisposable
{
    private readonly Guid _live = Guid.NewGuid();
    private readonly Guid _twin = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly ServiceProvider _services;

    public PromoteLeadSourceTests()
    {
        var me = Substitute.For<ICurrentUser>();
        me.Id.Returns(Guid.NewGuid());
        me.TenantId.Returns(_twin);
        me.TeamTenantId.Returns(_live);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(LeadsDbContext).Assembly));
        services.AddScoped<ICurrentUser>(_ => BackgroundJobContext.CurrentUser ?? me);
        services.AddScoped<ITenantContext>(_ => BackgroundJobContext.CurrentTenant ?? new FixedTenantContext(_twin));
        services.AddScoped(sp => new LeadsDbContext(
            new DbContextOptionsBuilder<LeadsDbContext>().UseInMemoryDatabase(_database).Options,
            sp.GetRequiredService<ITenantContext>(), LeadsTestEncryptor.Instance));
        services.AddKeyedSingleton(nameof(LeadsDbContext), Substitute.For<IEventPublisher>());
        services.AddScoped<ProductionReplay>();
        services.AddScoped<Sankore.Modules.Leads.Features.LeadSources.LeadSourceCopier>();
        services.AddScoped<Sankore.Modules.Leads.Features.ProjectTemplates.ProjectTemplateCopier>();
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    private static ServerWebhookSettings Webhook(string? vaultRef) => new() { SignatureCredentialVaultRef = vaultRef };

    private async Task<LeadSourceConfig> SeedAsync(Guid tenantId, string label, string? vaultRef)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LeadsDbContext>();
        var source = LeadSourceConfig.Create(tenantId, "PARTENAIRE", label, LeadChannelType.InboundWebhook, 0,
            IntegrationMode.ServerWebhook, settings: Webhook(vaultRef), testKeys: tenantId == _twin);
        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync();
        return source;
    }

    private async Task<Result<PromotionResult>> PromoteAsync(Guid id)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PromoteLeadSourceCommand(id, false));
    }

    private async Task<LeadSourceConfig> LiveAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LeadsDbContext>().LeadSourceConfigs
            .IgnoreQueryFilters().SingleAsync(s => s.TenantId == _live);
    }

    [Fact]
    public async Task A_source_new_to_production_is_a_draft_with_its_own_live_key_and_no_test_secret()
    {
        var test = await SeedAsync(_twin, "Courtier", vaultRef: "vault:twin-secret");

        var result = await PromoteAsync(test.Id);

        result.IsSuccess.Should().BeTrue(result.Error);
        var live = await LiveAsync();
        live.Status.Should().Be(LeadSourceStatus.Draft);
        live.PublicKey.Should().StartWith(TestModeKeys.LivePublicKey).And.NotBe(test.PublicKey);
        live.Settings.Should().BeOfType<ServerWebhookSettings>().Which.SignatureCredentialVaultRef.Should().BeNull();
    }

    [Fact]
    public async Task An_existing_production_source_takes_the_settings_and_keeps_its_key_and_secret()
    {
        var existing = await SeedAsync(_live, "Ancien libellé", vaultRef: "vault:live-secret");
        var test = await SeedAsync(_twin, "Courtier — nouveau mapping", vaultRef: "vault:twin-secret");

        var result = await PromoteAsync(test.Id);

        result.Value.Action.Should().Be(PromotionActions.Updated);
        var live = await LiveAsync();
        live.Id.Should().Be(existing.Id);
        live.Label.Should().Be("Courtier — nouveau mapping");
        live.PublicKey.Should().Be(existing.PublicKey);
        live.Settings.Should().BeOfType<ServerWebhookSettings>().Which.SignatureCredentialVaultRef.Should().Be("vault:live-secret");
    }
}

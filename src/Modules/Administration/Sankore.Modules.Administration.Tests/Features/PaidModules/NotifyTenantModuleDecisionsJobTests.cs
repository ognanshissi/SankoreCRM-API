namespace Sankore.Modules.Administration.Tests.Features.PaidModules;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.PaidModules;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.PaidModules;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The registry decides and sends nothing; this job reads its decisions and tells the requester.
/// What it must get right is WHICH rows are news — the outbox's idempotency does the rest.
/// </summary>
public sealed class NotifyTenantModuleDecisionsJobTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;
    private readonly FakeRegistry _registry = new();
    private readonly INotificationsModule _notifications = Substitute.For<INotificationsModule>();
    private readonly ServiceProvider _provider;
    private readonly Guid _userId;

    public NotifyTenantModuleDecisionsJobTests()
    {
        _factory = new TestAdminDbContextFactory(_tenantId);
        using (var db = _factory.CreateContext())
        {
            var user = AppUser.Create(_tenantId, Guid.NewGuid(), "Awa", "Koné", "awa@caisse.ci");
            db.Users.Add(user);
            db.SaveChanges();
            _userId = user.Id;
        }

        var tenants = Substitute.For<ITenantStore>();
        tenants.GetAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantInfo(_tenantId, "Caisse Avenir", "https://crm.caisse.ci", true, false, null, null));

        var services = new ServiceCollection();
        services.AddSingleton<ITenantModuleRegistry>(_registry);
        services.AddSingleton(_notifications);
        services.AddSingleton(tenants);
        services.AddScoped<AdministrationDbContext>(_ => _factory.CreateContext());
        services.AddSingleton(Options.Create(new TenantModuleNotificationOptions()));
        services.AddLogging();
        services.AddScoped<TenantModuleNotifier>();
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _factory.Dispose();
    }

    private Task<int> RunAsync() =>
        new NotifyTenantModuleDecisionsJob(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<ITenantStore>(),
                new FixedClock(Now),
                NullLogger<NotifyTenantModuleDecisionsJob>.Instance)
            .ExecuteForTenantAsync(_tenantId, default);

    private static RegistryModuleSubscription Row(string status, string source = "Request",
        DateTimeOffset? decidedAt = null, string? reason = null, bool activated = false) =>
        new(Guid.NewGuid(), TenantModules.Insurance, status, source, Now.AddDays(-3), Now.AddDays(-3),
            "awa@caisse.ci", 150000m, "XOF", decidedAt ?? Now.AddMinutes(-3), reason, null,
            activated ? Now.AddMinutes(-3) : null, null, null);

    private void Registry(params RegistryModuleSubscription[] history) =>
        _registry.Modules =
        [
            new RegistryTenantModule(TenantModules.Insurance, "Assurance", "", 150000m, "XOF", true,
                history.Last().Status, history.Last(), history),
        ];

    private List<QueueEmailRequest> Mails() =>
        [.. _notifications.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(INotificationsModule.QueueEmailAsync))
            .Select(c => (QueueEmailRequest)c.GetArguments()[0]!)];

    [Fact]
    public async Task An_approval_mails_the_requester_and_rings_their_bell()
    {
        var approved = Row("Active", activated: true);
        Registry(approved);

        (await RunAsync()).Should().Be(1);

        var mail = Mails().Should().ContainSingle().Subject;
        mail.TemplateKey.Should().Be(TenantModuleNotifier.ApprovedTemplate);
        mail.RecipientEmail.Should().Be("awa@caisse.ci");
        mail.IdempotencyKey.Should().Be($"tenant-module.decided:{approved.Id}",
            "the outbox drops a key it has seen: that is what makes every later run silent");
        mail.TemplateData["modules_url"].Should().Be("https://crm.caisse.ci/settings/modules");
        await _notifications.Received(1).NotifyInAppAsync(
            Arg.Is<InAppNotificationRequest>(n => n.UserId == _userId && n.Link == "/settings/modules"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refusal_carries_its_reason()
    {
        Registry(Row("Rejected", reason: "Contrat non signé"));

        await RunAsync();

        var mail = Mails().Should().ContainSingle().Subject;
        mail.TemplateKey.Should().Be(TenantModuleNotifier.RejectedTemplate);
        mail.TemplateData["reason"].Should().Be("Contrat non signé");
    }

    [Fact]
    public async Task An_approval_deactivated_since_is_still_announced_as_approved()
    {
        Registry(Row("Deactivated", activated: true));

        await RunAsync();

        Mails().Should().ContainSingle().Which.TemplateKey.Should().Be(TenantModuleNotifier.ApprovedTemplate);
    }

    [Fact]
    public async Task Pending_cancelled_direct_and_old_rows_are_not_news()
    {
        Registry(
            Row("Requested", decidedAt: null) with { DecidedAt = null },
            Row("Cancelled"),
            Row("Active", source: "Direct", activated: true),
            Row("Active", source: "Migration", activated: true),
            Row("Rejected", decidedAt: Now - NotifyTenantModuleDecisionsJob.Lookback - TimeSpan.FromMinutes(1)));

        (await RunAsync()).Should().Be(0);
        Mails().Should().BeEmpty();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

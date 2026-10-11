namespace Sankore.Modules.Administration.Tests.Infrastructure;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// <c>IAdministrationModule.NotifyUserAsync</c> — the seam other modules notify an agent through.
///
/// <para>
/// It exists because <c>AgentSummary</c> carries no email, so M13 was sending to
/// <c>{agentId}@agent.internal</c> and M13's SLA job to <c>{fullName}@notification.internal</c>:
/// two synthetic addresses nobody receives, failing silently. The address is deliberately NOT
/// returned to callers — this module owns it, together with the locale fallback that a template
/// resolved case-sensitively in PostgreSQL depends on.
/// </para>
/// </summary>
public sealed class NotifyUserTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;
    private readonly List<QueueEmailRequest> _queued = [];
    private readonly INotificationsModule _notifications = Substitute.For<INotificationsModule>();

    public NotifyUserTests()
    {
        _factory = new TestAdminDbContextFactory(_tenantId);

        _notifications
            .QueueEmailAsync(Arg.Do<QueueEmailRequest>(_queued.Add), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(Guid.NewGuid()));
    }

    public void Dispose() => _factory.Dispose();

    private ITenantStore TenantStore()
    {
        var store = Substitute.For<ITenantStore>();
        store.GetAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantInfo(
                _tenantId, "Sankore MFI", "sankore.ci", true, false, null, null, "fr"));
        return store;
    }

    private AdministrationModuleFacade Facade() => new(
        _factory.CreateContext(),
        new ProductCatalogCache(),
        new ModuleEmailSender(
            _notifications, TenantStore(), NullLogger<ModuleEmailSender>.Instance));

    private async Task<AppUser> SeedUserAsync(
        string? email = "awa@sankore.ci", string? preferredLanguage = null)
    {
        await using var db = _factory.CreateContext();

        var user = AppUser.Create(_tenantId, _agencyId, "Awa", "Ouattara", "awa@sankore.ci");

        // Email is required by the factory, so an address-less user is made by clearing it —
        // which is the real-world shape of an account imported without one.
        if (email is null)
            typeof(AppUser).GetProperty(nameof(AppUser.Email))!.SetValue(user, null);

        if (preferredLanguage is not null)
            user.SetPreferredLanguage(preferredLanguage);

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private NotifyUserRequest Request(Guid userId, Guid? tenantId = null) => new(
        TenantId: tenantId ?? _tenantId,
        UserId: userId,
        TemplateKey: "task.assigned",
        IdempotencyKey: $"task-assigned-{userId}",
        TemplateData: new Dictionary<string, object> { ["task_title"] = "Rappeler le prospect" });

    [Fact]
    public async Task A_known_user_is_notified()
    {
        var user = await SeedUserAsync();

        var sent = await Facade().NotifyUserAsync(Request(user.Id), CancellationToken.None);

        sent.Should().BeTrue();
        var queued = _queued.Should().ContainSingle().Subject;
        queued.RecipientEmail.Should().Be("awa@sankore.ci");
        queued.TemplateKey.Should().Be("task.assigned");
        queued.TenantId.Should().Be(_tenantId);
    }

    [Fact]
    public async Task The_recipients_name_and_the_tenant_name_are_added_for_the_template()
    {
        // Every template of the platform names the recipient with full_name, and the two that
        // used agent_name were moved onto it so there is one variable for "who this is to".
        var user = await SeedUserAsync();

        await Facade().NotifyUserAsync(Request(user.Id), CancellationToken.None);

        var data = _queued.Should().ContainSingle().Subject.TemplateData;
        data["full_name"].Should().Be("Awa Ouattara");
        data["company_name"].Should().Be("Sankore MFI");
        data["task_title"].Should().Be("Rappeler le prospect");
    }

    [Fact]
    public async Task An_unknown_user_is_false_and_not_an_exception()
    {
        // The callers are consumers and jobs whose own work has already happened. A deactivated
        // or deleted recipient must not have them retry for ever.
        var sent = await Facade().NotifyUserAsync(Request(Guid.NewGuid()), CancellationToken.None);

        sent.Should().BeFalse();
        _queued.Should().BeEmpty();
    }

    [Fact]
    public async Task A_user_of_another_tenant_is_not_reachable()
    {
        // The lookup pairs IgnoreQueryFilters with an explicit tenant predicate, the rule this
        // file follows throughout: a consumer's ambient tenant is not the tenant being notified.
        var user = await SeedUserAsync();

        var sent = await Facade().NotifyUserAsync(
            Request(user.Id, tenantId: Guid.NewGuid()), CancellationToken.None);

        sent.Should().BeFalse();
        _queued.Should().BeEmpty();
    }

    [Fact]
    public async Task A_user_without_an_address_is_false()
    {
        var user = await SeedUserAsync(email: null);

        var sent = await Facade().NotifyUserAsync(Request(user.Id), CancellationToken.None);

        sent.Should().BeFalse();
        _queued.Should().BeEmpty();
    }

    [Fact]
    public async Task The_users_own_locale_wins_over_the_tenant_default()
    {
        var user = await SeedUserAsync(preferredLanguage: "en");

        await Facade().NotifyUserAsync(Request(user.Id), CancellationToken.None);

        _queued.Should().ContainSingle().Which.Locale.Should().Be("en");
    }

    [Fact]
    public async Task A_facade_built_without_a_sender_fails_loudly()
    {
        // Optional only for the unit tests that construct the facade directly and notify nobody.
        // A notification silently disabled is the exact failure this whole change removes, so the
        // absent dependency must shout rather than return false.
        var facade = new AdministrationModuleFacade(
            _factory.CreateContext(), new ProductCatalogCache());

        var act = async () =>
            await facade.NotifyUserAsync(Request(Guid.NewGuid()), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}

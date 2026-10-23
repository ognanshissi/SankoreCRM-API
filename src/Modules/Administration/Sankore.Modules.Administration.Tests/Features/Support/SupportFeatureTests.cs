namespace Sankore.Modules.Administration.Tests.Features.Support;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Support;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.PaidModules;
using Sankore.Modules.Administration.Infrastructure.Support;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The tenant side of the support tickets: always the caller's own tenant and name, their own
/// tickets unless they hold the wide read, the platform mailed at the right moments.
/// </summary>
public sealed class SupportFeatureTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly FakeSupportRegistry _registry = new();
    private readonly INotificationsModule _notifications = Substitute.For<INotificationsModule>();
    private readonly ITenantStore _tenantStore = Substitute.For<ITenantStore>();
    private readonly Guid _userId;
    private string? _platformRecipient = "support@sankore.io";

    public SupportFeatureTests()
    {
        _factory = new TestAdminDbContextFactory(_tenantId);
        using var db = _factory.CreateContext();
        var user = AppUser.Create(_tenantId, Guid.NewGuid(), "Awa", "Koné", "awa@caisse.ci");
        db.Users.Add(user);
        db.SaveChanges();
        _userId = user.Id;

        _currentUser.Id.Returns(_userId);
        _currentUser.TenantId.Returns(_tenantId);
        _currentUser.IsAuthenticated.Returns(true);
        _currentUser.Roles.Returns(["Agent"]);
        _currentUser.Permissions.Returns(["support:read", "support:create"]);
        _tenantStore.GetAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantInfo(_tenantId, "Caisse Avenir", "https://crm.caisse.ci", true, false, null, null));
    }

    public void Dispose() => _factory.Dispose();

    private void GrantWideRead() =>
        _currentUser.Permissions.Returns(["support:read", "support:create", "support:read:all"]);

    private SupportNotifier Notifier(AdministrationDbContext db) =>
        new(_notifications, _tenantStore, db,
            Options.Create(new SupportNotificationOptions { PlatformRecipient = _platformRecipient, AdminAppUrl = "https://admin.sankore.io" }),
            NullLogger<SupportNotifier>.Instance);

    private List<QueueEmailRequest> QueuedEmails() =>
        [.. _notifications.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(INotificationsModule.QueueEmailAsync))
            .Select(c => (QueueEmailRequest)c.GetArguments()[0]!)];

    [Fact]
    public async Task Without_the_wide_read_the_list_is_narrowed_to_the_caller()
    {
        var result = await new ListSupportTicketsHandler(_registry, _currentUser)
            .Handle(new ListSupportTicketsQuery(null, false, false, null, 1, 25), default);

        result.IsSuccess.Should().BeTrue();
        _registry.Calls.Should().Equal($"list {_tenantId} {_userId}");
    }

    [Fact]
    public async Task With_the_wide_read_the_list_is_the_tenants_unless_mine_is_asked()
    {
        GrantWideRead();
        var handler = new ListSupportTicketsHandler(_registry, _currentUser);

        await handler.Handle(new ListSupportTicketsQuery(null, false, false, null, 1, 25), default);
        await handler.Handle(new ListSupportTicketsQuery(null, false, true, null, 1, 25), default);

        _registry.Calls.Should().Equal($"list {_tenantId} -", $"list {_tenantId} {_userId}");
    }

    [Fact]
    public async Task Opening_names_the_caller_from_the_user_table_and_mails_the_platform_address()
    {
        await using var db = _factory.CreateContext();
        var result = await new OpenSupportTicketHandler(_registry, _currentUser, db, Notifier(db))
            .Handle(new OpenSupportTicketCommand("Export bloqué", SupportTicketCategory.Bug, SupportTicketPriority.High,
                "L'export des leads ne finit jamais.", "/leads", "1.4.0", "Firefox"), default);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Reference.Should().Be("SUP-000042");
        _registry.Opened!.UserId.Should().Be(_userId);
        _registry.Opened.Email.Should().Be("awa@caisse.ci");
        _registry.Opened.Role.Should().Be("Agent");
        _registry.Opened.UserAgent.Should().Be("Firefox");

        var mail = QueuedEmails().Should().ContainSingle().Subject;
        mail.TemplateKey.Should().Be(SupportNotifier.OpenedTemplate);
        mail.RecipientEmail.Should().Be("support@sankore.io");
        mail.TemplateData["company_name"].Should().Be("Caisse Avenir");
        mail.TemplateData["priority"].Should().Be("Haute");
        mail.TemplateData["admin_url"].Should().Be($"https://admin.sankore.io/support/{_registry.OpenedId}");
    }

    [Fact]
    public async Task With_no_platform_address_the_ticket_is_still_opened()
    {
        _platformRecipient = null;

        await using var db = _factory.CreateContext();
        var result = await new OpenSupportTicketHandler(_registry, _currentUser, db, Notifier(db))
            .Handle(new OpenSupportTicketCommand("Question", SupportTicketCategory.Question, SupportTicketPriority.Normal,
                "Comment exporter ?", null, null), default);

        result.IsSuccess.Should().BeTrue();
        QueuedEmails().Should().BeEmpty();
    }

    [Fact]
    public async Task A_reply_is_confined_to_the_requester_without_the_wide_read_and_tells_the_platform()
    {
        await using var db = _factory.CreateContext();
        var result = await new ReplySupportTicketHandler(_registry, _currentUser, db, Notifier(db), TimeProvider.System)
            .Handle(new ReplySupportTicketCommand(_registry.TicketId, "Toujours bloqué"), default);

        result.IsSuccess.Should().BeTrue();
        _registry.Calls.Should().Contain($"reply {_tenantId} {_registry.TicketId} {_userId} requesterOnly=True");
        QueuedEmails().Should().ContainSingle(m => m.TemplateKey == SupportNotifier.TenantRepliedTemplate);
    }

    [Fact]
    public async Task The_registrys_codes_are_relayed_as_they_are()
    {
        _registry.ReplyAnswer = new RegistryResult<bool>(false, SupportTicketErrors.TicketClosed);

        await using var db = _factory.CreateContext();
        var result = await new ReplySupportTicketHandler(_registry, _currentUser, db, Notifier(db), TimeProvider.System)
            .Handle(new ReplySupportTicketCommand(_registry.TicketId, "Encore"), default);

        result.Error.Should().Be(SupportTicketErrors.TicketClosed);
        QueuedEmails().Should().BeEmpty();
    }

    [Fact]
    public async Task A_ticket_the_registry_does_not_return_reads_as_not_found()
    {
        _registry.Detail = null;

        var result = await new GetSupportTicketHandler(_registry, _currentUser)
            .Handle(new GetSupportTicketQuery(Guid.NewGuid()), default);

        result.Error.Should().Be(SupportTicketErrors.TicketNotFound);
    }

    [Fact]
    public async Task The_detail_says_what_the_caller_may_do_and_which_messages_are_theirs()
    {
        var result = await new GetSupportTicketHandler(_registry, _currentUser)
            .Handle(new GetSupportTicketQuery(_registry.TicketId), default);

        var dto = result.Value!;
        dto.CanReply.Should().BeFalse();
        dto.CanReopen.Should().BeTrue();
        dto.Ticket.IsMine.Should().BeTrue();
        dto.Messages.Should().HaveCount(2, "an internal note is never relayed");
        dto.Messages[0].IsMine.Should().BeTrue();
        dto.Messages[1].IsMine.Should().BeFalse();
    }

    [Fact]
    public async Task The_job_announces_a_reply_and_its_status_change_once()
    {
        var replyId = Guid.NewGuid();
        _registry.Activity =
        [
            Activity(replyId, SupportMessageKind.Reply, "Nous corrigeons.", null, Now.AddMinutes(-4)),
            Activity(Guid.NewGuid(), SupportMessageKind.StatusChange, null, SupportTicketStatus.WaitingOnTenant, Now.AddMinutes(-4)),
            Activity(Guid.NewGuid(), SupportMessageKind.StatusChange, null, SupportTicketStatus.Resolved, Now.AddMinutes(-1)),
        ];

        var notified = await RunJobAsync();

        notified.Should().Be(2);
        var mails = QueuedEmails();
        mails.Should().HaveCount(2);
        mails[0].TemplateKey.Should().Be(SupportNotifier.RepliedTemplate);
        mails[0].IdempotencyKey.Should().Be($"support-ticket.activity:{replyId}");
        mails[0].TemplateData["status_label"].Should().Be("En attente de votre réponse");
        mails[0].TemplateData["ticket_url"].Should().Be($"https://crm.caisse.ci/support/{_registry.TicketId}");
        mails[1].TemplateKey.Should().Be(SupportNotifier.StatusChangedTemplate);
        mails[1].TemplateData["status_label"].Should().Be("Résolu");
        await _notifications.Received(2).NotifyInAppAsync(
            Arg.Is<InAppNotificationRequest>(r => r.UserId == _userId && r.Link == $"/support/{_registry.TicketId}"),
            Arg.Any<CancellationToken>());
    }

    private RegistrySupportActivity Activity(Guid id, SupportMessageKind kind, string? body, SupportTicketStatus? status,
        DateTimeOffset at) =>
        new(id, _registry.TicketId, 42, "SUP-000042", "Export bloqué", _userId, "awa@caisse.ci", kind,
            SupportAuthorKind.Platform, "Équipe Sankoré", body, status, at);

    private async Task<int> RunJobAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISupportRegistry>(_registry);
        services.AddSingleton(_notifications);
        services.AddSingleton(_tenantStore);
        services.AddScoped<AdministrationDbContext>(_ => _factory.CreateContext());
        services.AddSingleton(Options.Create(new SupportNotificationOptions()));
        services.AddLogging();
        services.AddScoped<SupportNotifier>();
        await using var provider = services.BuildServiceProvider();

        return await new NotifySupportActivityJob(
                provider.GetRequiredService<IServiceScopeFactory>(), _tenantStore, new FixedClock(Now),
                NullLogger<NotifySupportActivityJob>.Instance)
            .ExecuteForTenantAsync(_tenantId, default);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeSupportRegistry : ISupportRegistry
    {
        public readonly Guid TicketId = Guid.NewGuid();
        public readonly Guid OpenedId = Guid.NewGuid();
        public List<string> Calls { get; } = [];
        public RegistryOpenTicket? Opened { get; private set; }
        public RegistryResult<bool> ReplyAnswer { get; set; } = new(true, null);
        /// <summary>Null = a closed ticket opened by whoever reads it, with a note and a status change.</summary>
        public RegistrySupportTicketDetail? Detail { get; set; }
        public IReadOnlyList<RegistrySupportActivity> Activity { get; set; } = [];
        public Guid RequesterId { get; set; }

        private RegistrySupportTicket Ticket(Guid requester, SupportTicketStatus status) =>
            new(TicketId, 42, "SUP-000042", Guid.Empty, "Export bloqué", SupportTicketCategory.Bug,
                SupportTicketPriority.High, status, requester, "Awa Koné", "awa@caisse.ci", Now, Now);

        public Task<RegistryResult<RegistrySupportPage>> ListAsync(Guid tenantId, Guid? requesterUserId,
            SupportTicketStatus? status, bool openOnly, string? search, int page, int pageSize, CancellationToken ct)
        {
            Calls.Add($"list {tenantId} {requesterUserId?.ToString() ?? "-"}");
            return Task.FromResult(new RegistryResult<RegistrySupportPage>(new RegistrySupportPage([], 0, page, pageSize), null));
        }

        public Task<RegistryResult<RegistrySupportOpened>> OpenAsync(Guid tenantId, RegistryOpenTicket ticket,
            CancellationToken ct)
        {
            Opened = ticket;
            return Task.FromResult(new RegistryResult<RegistrySupportOpened>(new RegistrySupportOpened(OpenedId, 42, "SUP-000042"), null));
        }

        public Task<RegistryResult<RegistrySupportTicketDetail?>> GetAsync(Guid tenantId, Guid ticketId, Guid? requesterUserId,
            CancellationToken ct)
        {
            Calls.Add($"get {tenantId} {ticketId} {requesterUserId?.ToString() ?? "-"}");
            if (ticketId != TicketId) return Task.FromResult(new RegistryResult<RegistrySupportTicketDetail?>(null, null));

            var me = requesterUserId ?? RequesterId;
            var detail = Detail ?? new RegistrySupportTicketDetail(Ticket(me, SupportTicketStatus.Closed), "/leads", null, Now,
            [
                new(Guid.NewGuid(), SupportMessageKind.Reply, SupportAuthorKind.Tenant, me, "Awa Koné", "Bloqué", null, Now),
                new(Guid.NewGuid(), SupportMessageKind.InternalNote, SupportAuthorKind.Platform, Guid.NewGuid(), "Ops", "Note", null, Now),
                new(Guid.NewGuid(), SupportMessageKind.StatusChange, SupportAuthorKind.Tenant, Guid.NewGuid(), "Collègue", null,
                    SupportTicketStatus.Closed, Now),
            ]);
            return Task.FromResult(new RegistryResult<RegistrySupportTicketDetail?>(detail, null));
        }

        public Task<RegistryResult<bool>> ReplyAsync(Guid tenantId, Guid ticketId, Guid userId, string fullName,
            bool requesterOnly, string body, CancellationToken ct)
        {
            Calls.Add($"reply {tenantId} {ticketId} {userId} requesterOnly={requesterOnly}");
            return Task.FromResult(ReplyAnswer);
        }

        public Task<RegistryResult<bool>> CloseAsync(Guid tenantId, Guid ticketId, Guid userId, string fullName,
            bool requesterOnly, CancellationToken ct)
        {
            Calls.Add($"close {tenantId} {ticketId} {userId} requesterOnly={requesterOnly}");
            return Task.FromResult(new RegistryResult<bool>(true, null));
        }

        public Task<RegistryResult<bool>> ReopenAsync(Guid tenantId, Guid ticketId, Guid userId, string fullName,
            bool requesterOnly, CancellationToken ct)
        {
            Calls.Add($"reopen {tenantId} {ticketId} {userId} requesterOnly={requesterOnly}");
            return Task.FromResult(new RegistryResult<bool>(true, null));
        }

        public Task<RegistryResult<IReadOnlyList<RegistrySupportActivity>>> ActivityAsync(Guid tenantId, DateTimeOffset since,
            CancellationToken ct) =>
            Task.FromResult(new RegistryResult<IReadOnlyList<RegistrySupportActivity>>(Activity, null));
    }
}

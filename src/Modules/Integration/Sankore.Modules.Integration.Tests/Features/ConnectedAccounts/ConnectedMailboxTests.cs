namespace Sankore.Modules.Integration.Tests.Features.ConnectedAccounts;

using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.ConnectedAccounts;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.Transport;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>Connected mailbox, lot 1: an IMAP/SMTP mailbox connected with a password.</summary>
public sealed class ConnectedMailboxTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private readonly TestIntegrationDbContextFactory _factory;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly InMemorySecrets _secrets = new();
    private readonly ScriptedProbe _probe = new();

    public ConnectedMailboxTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);
        _currentUser.Id.Returns(_me);
        _currentUser.TenantId.Returns(_tenant);
    }

    public void Dispose() => _factory.Dispose();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class InMemorySecrets : ISecretsModule
    {
        public ConcurrentDictionary<SecretKey, string> Entries { get; } = new();

        public Task SetAsync(SecretKey key, string value, DateTimeOffset? expiresAt = null, CancellationToken ct = default)
        {
            Entries[key] = value;
            return Task.CompletedTask;
        }

        public Task<SecretHint?> GetHintAsync(SecretKey key, CancellationToken ct = default) =>
            Task.FromResult(Entries.ContainsKey(key) ? new SecretHint(key.Name, "***", null) : null);

        public Task<string?> GetValueAsync(SecretKey key, CancellationToken ct = default) =>
            Task.FromResult(Entries.TryGetValue(key, out var v) ? v : null);

        public Task DeleteAllAsync(Guid tenantId, string scope, Guid entityId, CancellationToken ct = default)
        {
            foreach (var key in Entries.Keys.Where(k => k.TenantId == tenantId && k.Scope == scope && k.EntityId == entityId).ToList())
                Entries.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }

    /// <summary>Accepts one password, or fails with a scripted code; records what it was asked.</summary>
    private sealed class ScriptedProbe : IMailServerProbe
    {
        public string AcceptedPassword { get; set; } = "app-password";
        public string? FailWith { get; set; }
        public List<(MailServerEndpoints Endpoints, string Password)> Calls { get; } = [];

        public Task<MailServerTestResult> ProbeAsync(MailServerEndpoints endpoints, string password, CancellationToken ct)
        {
            Calls.Add((endpoints, password));
            if (FailWith is not null) return Task.FromResult(new MailServerTestResult(false, FailWith));
            return Task.FromResult(password == AcceptedPassword
                ? new MailServerTestResult(true, null)
                : new MailServerTestResult(false, "MAIL_IMAP_AUTH_FAILED"));
        }
    }

    private static ImapMailboxRequest Request(string? password = "app-password", string username = "awa@cabinet.ci", string imapHost = "mail.cabinet.ci") => new(
        Email: "awa@cabinet.ci", DisplayName: "Awa Koné", Label: "Cabinet",
        ImapHost: imapHost, ImapPort: 993, ImapSecurity: "Ssl",
        SmtpHost: "smtp.cabinet.ci", SmtpPort: 587, SmtpSecurity: "StartTls",
        Username: username, Password: password);

    // ── Settings ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://mail.cabinet.ci", 993, "Ssl", 587, "MAIL_IMAP_HOST_INVALID")]
    [InlineData("mail.cabinet.ci:993", 993, "Ssl", 587, "MAIL_IMAP_HOST_INVALID")]
    [InlineData("mail.cabinet.ci", 22, "Ssl", 587, "MAIL_IMAP_PORT_NOT_ALLOWED")]
    [InlineData("mail.cabinet.ci", 993, "Ssl", 6379, "MAIL_SMTP_PORT_NOT_ALLOWED")]
    [InlineData("mail.cabinet.ci", 993, "None", 587, "MAIL_SECURITY_INVALID")]
    public async Task Settings_outside_the_rules_are_refused_before_any_connection(
        string imapHost, int imapPort, string imapSecurity, int smtpPort, string code)
    {
        var request = Request() with { ImapHost = imapHost, ImapPort = imapPort, ImapSecurity = imapSecurity, SmtpPort = smtpPort };

        (await ConnectAsync(request)).Error.Should().Be(code);
        _probe.Calls.Should().BeEmpty("a port outside the list must not even be tried — the host is typed by a user");
    }

    [Fact]
    public async Task An_address_with_a_display_name_is_not_an_address()
    {
        (await ConnectAsync(Request() with { Email = "Awa <awa@cabinet.ci>" })).Error.Should().Be(MailServerErrors.EmailInvalid);
    }

    // ── Connect ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_mailbox_both_servers_accept_is_connected_with_its_password_in_the_vault_only()
    {
        var result = await ConnectAsync(Request() with { ImapHost = " Mail.Cabinet.CI. " });

        result.IsSuccess.Should().BeTrue(result.Error);
        var dto = result.Value!;
        dto.Provider.Should().Be("Imap");
        dto.MailEnabled.Should().BeTrue();
        dto.Calendars.Should().BeEmpty();
        dto.MailServer.Should().Be(new MailServerDto("mail.cabinet.ci", 993, "Ssl", "smtp.cabinet.ci", 587, "StartTls", "awa@cabinet.ci"));

        await using var db = _factory.CreateContext();
        var account = await db.ConnectedAccounts.SingleAsync();
        account.Provider.Should().Be(ConnectedAccountProvider.Imap);
        account.Label.Should().Be("Cabinet");
        account.ExternalAccountId.Should().Be("awa@cabinet.ci@mail.cabinet.ci");
        account.HasCalendars.Should().BeFalse();

        _secrets.Entries[ConnectedAccountSecrets.MailPassword(_tenant, account.Id)].Should().Be("app-password");
        (await db.MailServers.SingleAsync()).AccountId.Should().Be(account.Id);
    }

    [Fact]
    public async Task A_refused_login_saves_nothing_and_says_which_server_refused()
    {
        _probe.FailWith = "MAIL_SMTP_AUTH_FAILED";

        (await ConnectAsync(Request())).Error.Should().Be("MAIL_SMTP_AUTH_FAILED");

        await using var db = _factory.CreateContext();
        (await db.ConnectedAccounts.CountAsync()).Should().Be(0);
        (await db.MailServers.CountAsync()).Should().Be(0);
        _secrets.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_login_on_the_same_server_connected_again_updates_the_mailbox()
    {
        await ConnectAsync(Request());
        _probe.AcceptedPassword = "new-password";

        (await ConnectAsync(Request(password: "new-password") with { SmtpPort = 465, SmtpSecurity = "Ssl" })).IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        var account = await db.ConnectedAccounts.SingleAsync();
        (await db.MailServers.SingleAsync()).SmtpPort.Should().Be(465);
        _secrets.Entries[ConnectedAccountSecrets.MailPassword(_tenant, account.Id)].Should().Be("new-password");
    }

    [Fact]
    public void The_password_never_reaches_the_audit_trail()
    {
        var payload = SanitizedJsonSerializer.Serialize(ConnectImapMailboxCommand.From(Request(password: "s3cret-app-pw")));
        payload.Should().NotContain("s3cret-app-pw");

        var update = SanitizedJsonSerializer.Serialize(UpdateImapMailboxCommand.From(Guid.NewGuid(), Request(password: "s3cret-app-pw")));
        update.Should().NotContain("s3cret-app-pw");
    }

    // ── Update, test ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Changing_the_servers_without_retyping_the_password_tries_the_stored_one_and_keeps_it()
    {
        var id = (await ConnectAsync(Request())).Value!.Id;

        await using var db = _factory.CreateContext();
        var result = await new UpdateImapMailboxHandler(db, _currentUser, _secrets, _probe, new FixedClock(_now))
            .Handle(UpdateImapMailboxCommand.From(id, Request(password: null) with { ImapHost = "imap.cabinet.ci" }), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);
        _probe.Calls[^1].Password.Should().Be("app-password");
        _probe.Calls[^1].Endpoints.ImapHost.Should().Be("imap.cabinet.ci");
        result.Value!.MailServer!.ImapHost.Should().Be("imap.cabinet.ci");
        _secrets.Entries[ConnectedAccountSecrets.MailPassword(_tenant, id)].Should().Be("app-password");
    }

    [Fact]
    public async Task A_mailbox_needing_reconnection_is_active_again_once_its_new_password_works()
    {
        var id = (await ConnectAsync(Request())).Value!.Id;
        await using (var db = _factory.CreateContext())
        {
            (await db.ConnectedAccounts.AsTracking().SingleAsync()).MarkNeedsReconnect("MAIL_IMAP_AUTH_FAILED");
            await db.SaveChangesAsync();
        }

        _probe.AcceptedPassword = "changed";
        await using var update = _factory.CreateContext();
        (await new UpdateImapMailboxHandler(update, _currentUser, _secrets, _probe, new FixedClock(_now))
            .Handle(UpdateImapMailboxCommand.From(id, Request(password: "changed")), CancellationToken.None)).IsSuccess.Should().BeTrue();

        (await update.ConnectedAccounts.SingleAsync()).Status.Should().Be(ConnectedAccountStatus.Active);
        _secrets.Entries[ConnectedAccountSecrets.MailPassword(_tenant, id)].Should().Be("changed");
    }

    [Fact]
    public async Task An_update_cannot_turn_a_mailbox_into_another_one_already_connected()
    {
        await ConnectAsync(Request());
        var second = (await ConnectAsync(Request(username: "contact@cabinet.ci"))).Value!.Id;

        await using var db = _factory.CreateContext();
        (await new UpdateImapMailboxHandler(db, _currentUser, _secrets, _probe, new FixedClock(_now))
                .Handle(UpdateImapMailboxCommand.From(second, Request()), CancellationToken.None))
            .Error.Should().Be(MailServerErrors.AlreadyConnected);
    }

    [Fact]
    public async Task Testing_uses_the_stored_password_of_the_caller_s_own_mailbox_only()
    {
        var id = (await ConnectAsync(Request())).Value!.Id;

        await using var db = _factory.CreateContext();
        (await new TestImapMailboxHandler(db, _currentUser, _secrets, _probe)
                .Handle(new TestImapMailboxQuery(Request(password: null), id), CancellationToken.None))
            .Value!.Ok.Should().BeTrue();

        var colleague = Substitute.For<ICurrentUser>();
        colleague.Id.Returns(Guid.NewGuid());
        colleague.TenantId.Returns(_tenant);

        (await new TestImapMailboxHandler(db, colleague, _secrets, _probe)
                .Handle(new TestImapMailboxQuery(Request(password: null), id), CancellationToken.None))
            .Error.Should().Be(MailServerErrors.PasswordRequired, "a colleague cannot borrow somebody's stored password to log in");
    }

    // ── Disable, remove, agendas ───────────────────────────────────────────────

    [Fact]
    public async Task An_IMAP_mailbox_is_removed_rather_than_turned_off_and_removing_it_drops_its_servers_and_password()
    {
        var id = (await ConnectAsync(Request())).Value!.Id;

        await using var db = _factory.CreateContext();
        (await new DisableMailboxHandler(db, _currentUser).Handle(new DisableMailboxCommand(id), CancellationToken.None))
            .Error.Should().Be("CONNECTED_MAILBOX_IMAP_REMOVE_INSTEAD");

        var providers = new ConnectedAccountProviders([]);
        var tokens = new ConnectedAccountTokens(db, _secrets, providers, NullLogger<ConnectedAccountTokens>.Instance);
        var copies = new AppointmentCalendarCopies(db, Substitute.For<Sankore.Modules.Leads.PublicApi.ILeadsModule>(), providers, tokens,
            TimeProvider.System, NullLogger<AppointmentCalendarCopies>.Instance);

        (await new RemoveConnectedAccountHandler(db, _currentUser, _secrets, providers, copies, NullLogger<RemoveConnectedAccountHandler>.Instance)
            .Handle(new RemoveConnectedAccountCommand(id), CancellationToken.None)).IsSuccess.Should().BeTrue();

        (await db.ConnectedAccounts.CountAsync()).Should().Be(0);
        (await db.MailServers.CountAsync()).Should().Be(0);
        _secrets.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task The_agenda_sync_leaves_an_IMAP_mailbox_alone()
    {
        var id = (await ConnectAsync(Request())).Value!.Id;

        await using var db = _factory.CreateContext();
        var account = await db.ConnectedAccounts.AsTracking().SingleAsync(a => a.Id == id);
        var providers = new ConnectedAccountProviders([]);
        var sync = new ConnectedCalendarSync(db, new ConnectedAccountTokens(db, _secrets, providers, NullLogger<ConnectedAccountTokens>.Instance),
            providers, new CalendarEventProtector(Substitute.For<IFieldEncryptor>()), new FixedClock(_now), NullLogger<ConnectedCalendarSync>.Instance);

        var result = await sync.SyncAccountAsync(account, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("there is no provider to find for IMAP — that is not a failure");
        result.Value!.Calendars.Should().Be(0);
        account.LastSyncAt.Should().BeNull("nothing was synced: the mailbox sync records its own runs");
    }

    // ── Egress ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("169.254.169.254")]
    [InlineData("::ffff:192.168.1.10")]
    [InlineData("localhost")]
    public async Task A_mail_host_inside_our_own_network_is_refused(string host)
    {
        var egress = new MailServerEgress(Options.Create(new IntegrationEgressOptions()), NullLogger<MailServerEgress>.Instance);
        (await egress.ResolveAsync(host, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_self_hosted_deployment_may_allow_private_mail_hosts_and_a_public_address_always_passes()
    {
        var allowed = new MailServerEgress(Options.Create(new IntegrationEgressOptions { AllowPrivateAddresses = true }), NullLogger<MailServerEgress>.Instance);
        (await allowed.ResolveAsync("10.0.0.5", CancellationToken.None)).Should().Be(IPAddress.Parse("10.0.0.5"));

        var strict = new MailServerEgress(Options.Create(new IntegrationEgressOptions()), NullLogger<MailServerEgress>.Instance);
        (await strict.ResolveAsync("8.8.8.8", CancellationToken.None)).Should().Be(IPAddress.Parse("8.8.8.8"));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Result<ConnectedAccountDto>> ConnectAsync(ImapMailboxRequest request)
    {
        await using var db = _factory.CreateContext();
        return await new ConnectImapMailboxHandler(db, _currentUser, _secrets, _probe, new FixedClock(_now))
            .Handle(ConnectImapMailboxCommand.From(request), CancellationToken.None);
    }
}

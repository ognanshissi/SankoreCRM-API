namespace Sankore.Modules.Integration.Features.ConnectedAccounts;

using System.Net;
using MailAddress = System.Net.Mail.MailAddress;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Hangfire;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.Transport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Egress;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

// Connected mailbox, lot 1 (owner, 2026-10-27): a user reads and sends their e-mail from the CRM.
// Microsoft and Google accounts turn it on through their consent (ConnectedAccountsFeature,
// `includeMail`); every other mailbox is connected here, through IMAP and SMTP with a password.

// ── DTOs ─────────────────────────────────────────────────────────────────────────────────────

/// <param name="ImapSecurity"><c>Ssl</c> or <c>StartTls</c>.</param>
public sealed record MailServerDto(
    string ImapHost,
    int ImapPort,
    string ImapSecurity,
    string SmtpHost,
    int SmtpPort,
    string SmtpSecurity,
    string Username)
{
    internal static MailServerDto From(MailServerSettings s) => new(
        s.ImapHost, s.ImapPort, s.ImapSecurity.ToString(), s.SmtpHost, s.SmtpPort, s.SmtpSecurity.ToString(), s.Username);
}

/// <param name="Email">The address the mailbox sends from.</param>
/// <param name="Username">The login on both servers — usually the address.</param>
/// <param name="Password">
/// Required to connect; on an update, null keeps the stored one. Gmail, Outlook.com and Yahoo
/// refuse the account password over IMAP — they want an application password.
/// </param>
public sealed record ImapMailboxRequest(
    string Email,
    string? DisplayName,
    string? Label,
    string ImapHost,
    int ImapPort,
    string ImapSecurity,
    string SmtpHost,
    int SmtpPort,
    string SmtpSecurity,
    string Username,
    string? Password);

/// <param name="Ok">Both servers accepted the login.</param>
/// <param name="Code">
/// The first failure, when not ok: <c>MAIL_IMAP_UNREACHABLE</c>, <c>MAIL_IMAP_TLS_FAILED</c>,
/// <c>MAIL_IMAP_AUTH_FAILED</c>, and the same three for <c>SMTP</c>.
/// </param>
public sealed record MailServerTestResult(bool Ok, string? Code);

// ── Errors ───────────────────────────────────────────────────────────────────────────────────

internal static class MailServerErrors
{
    public const string EmailInvalid = "MAIL_EMAIL_INVALID";
    public const string PasswordRequired = "MAIL_PASSWORD_REQUIRED";
    public const string AlreadyConnected = "CONNECTED_MAILBOX_ALREADY_CONNECTED";
    public const string NotImap = "CONNECTED_MAILBOX_NOT_IMAP";

    public static string Unreachable(MailServerStep step) => $"MAIL_{Name(step)}_UNREACHABLE";
    public static string TlsFailed(MailServerStep step) => $"MAIL_{Name(step)}_TLS_FAILED";
    public static string AuthenticationFailed(MailServerStep step) => $"MAIL_{Name(step)}_AUTH_FAILED";

    private static string Name(MailServerStep step) => step == MailServerStep.Imap ? "IMAP" : "SMTP";
}

internal enum MailServerStep
{
    Imap,
    Smtp,
}

// ── Egress ───────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Resolves a mail host and refuses one inside SANKORÉ's own network — the shared block list
/// (<see cref="EgressAddressPolicy.IsBlocked"/>) and the SFTP deposits' setting
/// (<c>Integration:Egress:AllowPrivateAddresses</c>). Here the host is typed by ANY user, not by
/// an administrator, which makes the check more necessary, not less: pointed at 127.0.0.1:25 or a
/// 10.x address, the server would hand an internal service a login attempt.
///
/// <para>
/// The caller connects to the returned ADDRESS and never re-resolves the name (DNS rebinding).
/// A refusal is reported like a host that does not answer, so the screen cannot be used to map
/// which internal names exist.
/// </para>
/// </summary>
internal sealed class MailServerEgress(IOptions<IntegrationEgressOptions> options, ILogger<MailServerEgress> logger)
{
    public async Task<IPAddress?> ResolveAsync(string host, CancellationToken ct)
    {
        var allowPrivate = options.Value.AllowPrivateAddresses;

        if (IPAddress.TryParse(host, out var literal))
            return allowPrivate || !EgressAddressPolicy.IsBlocked(literal) ? literal : Refused(host);

        IPAddress[] addresses;

        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return null;
        }

        if (addresses.Length == 0) return null;
        if (allowPrivate) return addresses[0];

        // IPv4 first: a mail host announcing an AAAA record this network cannot route is common.
        return addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                   .FirstOrDefault(a => !EgressAddressPolicy.IsBlocked(a))
               ?? Refused(host);
    }

    private IPAddress? Refused(string host)
    {
        logger.LogWarning(
            "Refusing a mail server connection to '{Host}': it resolves only into private, loopback, link-local or CGNAT space. "
            + "A self-hosted deployment may set {Setting}:AllowPrivateAddresses.", host, IntegrationEgressOptions.SectionName);
        return null;
    }
}

// ── Probe ────────────────────────────────────────────────────────────────────────────────────

/// <summary>Logs into the IMAP server, then the SMTP server, and says which step failed first.</summary>
internal interface IMailServerProbe
{
    Task<MailServerTestResult> ProbeAsync(MailServerEndpoints endpoints, string password, CancellationToken ct);
}

/// <summary>
/// The MailKit probe: a login on each server through <see cref="MailServerConnector"/>, then a
/// clean disconnect. Nothing is read or sent.
/// </summary>
internal sealed class MailKitMailServerProbe(MailServerConnector connector) : IMailServerProbe
{
    public async Task<MailServerTestResult> ProbeAsync(MailServerEndpoints endpoints, string password, CancellationToken ct)
    {
        using (var imap = new ImapClient())
        {
            var failed = await connector.LoginAsync(imap, MailServerStep.Imap, endpoints.ImapHost, endpoints.ImapPort, endpoints.ImapSecurity, endpoints.Username, password, ct);
            if (failed is not null) return new MailServerTestResult(false, failed);
            await connector.DisconnectQuietlyAsync(imap);
        }

        using var smtp = new SmtpClient();
        var smtpFailed = await connector.LoginAsync(smtp, MailServerStep.Smtp, endpoints.SmtpHost, endpoints.SmtpPort, endpoints.SmtpSecurity, endpoints.Username, password, ct);
        if (smtpFailed is not null) return new MailServerTestResult(false, smtpFailed);

        await connector.DisconnectQuietlyAsync(smtp);
        return new MailServerTestResult(true, null);
    }
}

/// <summary>
/// The one way this module opens a mail server connection — the probe (lot 1) and the IMAP sync
/// (lot 2) alike. The connection goes to the address <see cref="MailServerEgress"/> validated,
/// through a socket opened here; MailKit is given the HOST name only for TLS (SNI and the
/// certificate check), which therefore still verifies the certificate against what the user typed.
/// </summary>
internal sealed class MailServerConnector(MailServerEgress egress, ILogger<MailServerConnector> logger)
{
    /// <summary>Per connection and per command. A server that has not answered in this long will not do better.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Connects and logs in. Null on success — the client stays connected — else the error code.</summary>
    public async Task<string?> LoginAsync(
        MailService client, MailServerStep step, string host, int port, MailServerSecurity security, string username, string password, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        client.Timeout = (int)Timeout.TotalMilliseconds;
        client.CheckCertificateRevocation = true;
        client.ServerCertificateValidationCallback = MailServerCertificates.Validate;

        try
        {
            var address = await egress.ResolveAsync(host, timeout.Token);
            if (address is null) return MailServerErrors.Unreachable(step);

            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                await socket.ConnectAsync(address, port, timeout.Token);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            await client.ConnectAsync(socket, host, port, Options(security), timeout.Token);

            // A password is what the user gave: never let MailKit try an OAuth mechanism instead.
            client.AuthenticationMechanisms.Remove("XOAUTH2");
            client.AuthenticationMechanisms.Remove("OAUTHBEARER");

            await client.AuthenticateAsync(username, password, timeout.Token);
            return null;
        }
        catch (AuthenticationException)
        {
            return MailServerErrors.AuthenticationFailed(step);
        }
        catch (SmtpCommandException ex) when (ex.StatusCode is SmtpStatusCode.AuthenticationRequired or SmtpStatusCode.AuthenticationChallenge)
        {
            return MailServerErrors.AuthenticationFailed(step);
        }
        catch (Exception ex) when (ex is SslHandshakeException or NotSupportedException)
        {
            // NotSupportedException: the server offers no STARTTLS — the connection would stay in clear.
            return MailServerErrors.TlsFailed(step);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return MailServerErrors.Unreachable(step);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ProtocolException or CommandException or TimeoutException)
        {
            logger.LogInformation("Mail server connection ({Step}) to {Host}:{Port} failed: {Reason}.", step, host, port, ex.GetType().Name);
            return MailServerErrors.Unreachable(step);
        }
    }

    public async Task DisconnectQuietlyAsync(MailService client)
    {
        try
        {
            if (client.IsConnected) await client.DisconnectAsync(quit: true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or ProtocolException or CommandException or SocketException or OperationCanceledException)
        {
            // The server hung up first: nothing left to close.
        }
    }

    /// <summary>STARTTLS is REQUIRED, never « when available »: a server stripping it would get the password in clear.</summary>
    private static SecureSocketOptions Options(MailServerSecurity security) => security switch
    {
        MailServerSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.SslOnConnect,
    };
}

/// <summary>
/// The certificate rule of every mail server connection: the chain and the name must be right, a
/// REVOKED certificate is refused — and a revocation status nobody could fetch is not held against
/// the server. MailKit's own callback refuses that last case, and it is common: measured on
/// imap.gmail.com from a host whose OCSP/CRL requests do not get out, a valid Google certificate
/// came back <c>RevocationStatusUnknown</c> and every login read « TLS échoué ». Browsers soft-fail
/// the same way; refusing would make one unreachable OCSP responder take every mailbox down.
/// </summary>
internal static class MailServerCertificates
{
    private static readonly HashSet<X509ChainStatusFlags> Unverifiable =
    [
        X509ChainStatusFlags.RevocationStatusUnknown,
        X509ChainStatusFlags.OfflineRevocation,
    ];

    public static bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (errors != SslPolicyErrors.RemoteCertificateChainErrors || chain is null) return false;

        return chain.ChainStatus.All(s => s.Status == X509ChainStatusFlags.NoError || Unverifiable.Contains(s.Status));
    }
}

// ── Test settings without saving ─────────────────────────────────────────────────────────────

/// <summary>
/// « Tester la connexion »: nothing is written. Not an <see cref="ICommand"/>, so no transaction
/// and no audit row carrying the attempt; the password is used once and dropped.
/// </summary>
internal sealed record TestImapMailboxQuery(ImapMailboxRequest Request, Guid? AccountId) : IRequest<Result<MailServerTestResult>>;

internal sealed class TestImapMailboxHandler(
    IntegrationDbContext db, ICurrentUser me, ISecretsModule secrets, IMailServerProbe probe)
    : IRequestHandler<TestImapMailboxQuery, Result<MailServerTestResult>>
{
    public async Task<Result<MailServerTestResult>> Handle(TestImapMailboxQuery query, CancellationToken ct)
    {
        var endpoints = ImapMailboxInput.Endpoints(query.Request);
        if (endpoints.IsFailure) return Result.Fail<MailServerTestResult>(endpoints.Error!);

        var password = query.Request.Password;

        // Testing an edited mailbox without retyping its password: the stored one, of the caller's own account only.
        if (string.IsNullOrEmpty(password) && query.AccountId is { } accountId
            && await db.ConnectedAccounts.AnyAsync(a => a.Id == accountId && a.UserId == me.Id && a.Provider == ConnectedAccountProvider.Imap, ct))
        {
            password = await ConnectedAccountSecrets.OutsideTransactionAsync(() =>
                secrets.GetValueAsync(ConnectedAccountSecrets.MailPassword(me.TenantId, accountId), ct));
        }

        if (string.IsNullOrEmpty(password)) return Result.Fail<MailServerTestResult>(MailServerErrors.PasswordRequired);

        return Result.Ok(await probe.ProbeAsync(endpoints.Value!, password, ct));
    }
}

// ── Connect ──────────────────────────────────────────────────────────────────────────────────

internal sealed record ConnectImapMailboxCommand(
    string Email,
    string? DisplayName,
    string? Label,
    string ImapHost,
    int ImapPort,
    string ImapSecurity,
    string SmtpHost,
    int SmtpPort,
    string SmtpSecurity,
    string Username,
    [property: SensitiveData] string? Password)
    : IRequest<Result<ConnectedAccountDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "ConnectedAccount";
    public string? ResourceId => Email;

    public static ConnectImapMailboxCommand From(ImapMailboxRequest r) => new(
        r.Email, r.DisplayName, r.Label, r.ImapHost, r.ImapPort, r.ImapSecurity, r.SmtpHost, r.SmtpPort, r.SmtpSecurity, r.Username, r.Password);

    public ImapMailboxRequest AsRequest() => new(
        Email, DisplayName, Label, ImapHost, ImapPort, ImapSecurity, SmtpHost, SmtpPort, SmtpSecurity, Username, Password);
}

/// <summary>
/// Connects an IMAP/SMTP mailbox: BOTH servers must accept the login first — a mailbox saved
/// broken would show « connectée » and fail on every sync. The same login on the same server
/// connected again updates the existing account instead of adding a second one.
/// </summary>
internal sealed class ConnectImapMailboxHandler(
    IntegrationDbContext db, ICurrentUser me, ISecretsModule secrets, IMailServerProbe probe, TimeProvider clock,
    IBackgroundJobClient? jobs = null)
    : IRequestHandler<ConnectImapMailboxCommand, Result<ConnectedAccountDto>>
{
    public async Task<Result<ConnectedAccountDto>> Handle(ConnectImapMailboxCommand cmd, CancellationToken ct)
    {
        var request = cmd.AsRequest();
        var endpoints = ImapMailboxInput.Endpoints(request);
        if (endpoints.IsFailure) return Result.Fail<ConnectedAccountDto>(endpoints.Error!);

        var email = ImapMailboxInput.Email(request.Email);
        if (email is null) return Result.Fail<ConnectedAccountDto>(MailServerErrors.EmailInvalid);
        if (string.IsNullOrEmpty(cmd.Password)) return Result.Fail<ConnectedAccountDto>(MailServerErrors.PasswordRequired);

        // Before any read: the probe can take seconds and must not hold a connection of the transaction.
        var probed = await probe.ProbeAsync(endpoints.Value!, cmd.Password, ct);
        if (!probed.Ok) return Result.Fail<ConnectedAccountDto>(probed.Code!);

        var e = endpoints.Value!;
        var now = clock.GetUtcNow();
        var externalId = ConnectedAccount.ImapExternalId(e.Username, e.ImapHost);

        var account = await db.ConnectedAccounts.AsTracking().FirstOrDefaultAsync(a =>
            a.UserId == me.Id && a.Provider == ConnectedAccountProvider.Imap && a.ExternalAccountId == externalId, ct);

        MailServerSettings? server;

        if (account is null)
        {
            account = ConnectedAccount.ConnectImap(me.TenantId, me.Id, email, cmd.DisplayName, cmd.Label, e.Username, e.ImapHost, now);
            db.ConnectedAccounts.Add(account);

            var created = MailServerSettings.Create(me.TenantId, account.Id, me.Id, e, now);
            if (created.IsFailure) return Result.Fail<ConnectedAccountDto>(created.Error!);
            server = created.Value!;
            db.MailServers.Add(server);
        }
        else
        {
            account.UpdateImapIdentity(email, cmd.DisplayName, e.Username, e.ImapHost, now);
            if (!string.IsNullOrWhiteSpace(cmd.Label)) account.Rename(cmd.Label);

            server = await db.MailServers.AsTracking().FirstOrDefaultAsync(m => m.AccountId == account.Id, ct);
            if (server is null)
            {
                server = MailServerSettings.Create(me.TenantId, account.Id, me.Id, e, now).Value!;
                db.MailServers.Add(server);
            }
            else
            {
                server.Update(e, now);
            }
        }

        await db.SaveChangesAsync(ct);
        await ConnectedAccountSecrets.OutsideTransactionAsync(() =>
            secrets.SetAsync(ConnectedAccountSecrets.MailPassword(me.TenantId, account.Id), cmd.Password, null, ct));

        // The first read, in the background: the window can take minutes.
        var (tenantId, accountId) = (me.TenantId, account.Id);
        jobs?.Enqueue<SyncConnectedMailboxJob>(job => job.ExecuteAsync(tenantId, accountId));

        return Result.Ok(ListMyConnectedAccountsHandler.ToDto(account, [], server));
    }
}

// ── Update ───────────────────────────────────────────────────────────────────────────────────

internal sealed record UpdateImapMailboxCommand(
    Guid AccountId,
    string Email,
    string? DisplayName,
    string? Label,
    string ImapHost,
    int ImapPort,
    string ImapSecurity,
    string SmtpHost,
    int SmtpPort,
    string SmtpSecurity,
    string Username,
    [property: SensitiveData] string? Password)
    : IRequest<Result<ConnectedAccountDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "ConnectedAccount";
    public string? ResourceId => AccountId.ToString();

    public static UpdateImapMailboxCommand From(Guid accountId, ImapMailboxRequest r) => new(
        accountId, r.Email, r.DisplayName, r.Label, r.ImapHost, r.ImapPort, r.ImapSecurity, r.SmtpHost, r.SmtpPort, r.SmtpSecurity, r.Username, r.Password);

    public ImapMailboxRequest AsRequest() => new(
        Email, DisplayName, Label, ImapHost, ImapPort, ImapSecurity, SmtpHost, SmtpPort, SmtpSecurity, Username, Password);
}

/// <summary>
/// New servers, login or password — after a provider's change, or a mailbox « à reconnecter »
/// because its password changed. Probed like a connection; a null password keeps the stored one.
/// </summary>
internal sealed class UpdateImapMailboxHandler(
    IntegrationDbContext db, ICurrentUser me, ISecretsModule secrets, IMailServerProbe probe, TimeProvider clock,
    IBackgroundJobClient? jobs = null)
    : IRequestHandler<UpdateImapMailboxCommand, Result<ConnectedAccountDto>>
{
    public async Task<Result<ConnectedAccountDto>> Handle(UpdateImapMailboxCommand cmd, CancellationToken ct)
    {
        var request = cmd.AsRequest();
        var endpoints = ImapMailboxInput.Endpoints(request);
        if (endpoints.IsFailure) return Result.Fail<ConnectedAccountDto>(endpoints.Error!);

        var email = ImapMailboxInput.Email(request.Email);
        if (email is null) return Result.Fail<ConnectedAccountDto>(MailServerErrors.EmailInvalid);

        var account = await db.ConnectedAccounts.AsTracking().FirstOrDefaultAsync(a => a.Id == cmd.AccountId && a.UserId == me.Id, ct);
        if (account is null) return Result.Fail<ConnectedAccountDto>(ConnectedAccountErrors.NotFound);
        if (account.Provider != ConnectedAccountProvider.Imap) return Result.Fail<ConnectedAccountDto>(MailServerErrors.NotImap);

        var e = endpoints.Value!;
        var externalId = ConnectedAccount.ImapExternalId(e.Username, e.ImapHost);

        if (await db.ConnectedAccounts.AnyAsync(a => a.Id != account.Id && a.UserId == me.Id
                                                    && a.Provider == ConnectedAccountProvider.Imap && a.ExternalAccountId == externalId, ct))
        {
            return Result.Fail<ConnectedAccountDto>(MailServerErrors.AlreadyConnected);
        }

        var password = cmd.Password;
        if (string.IsNullOrEmpty(password))
        {
            password = await ConnectedAccountSecrets.OutsideTransactionAsync(() =>
                secrets.GetValueAsync(ConnectedAccountSecrets.MailPassword(me.TenantId, account.Id), ct));
        }

        if (string.IsNullOrEmpty(password)) return Result.Fail<ConnectedAccountDto>(MailServerErrors.PasswordRequired);

        var probed = await probe.ProbeAsync(e, password, ct);
        if (!probed.Ok) return Result.Fail<ConnectedAccountDto>(probed.Code!);

        var now = clock.GetUtcNow();
        account.UpdateImapIdentity(email, cmd.DisplayName, e.Username, e.ImapHost, now);
        if (!string.IsNullOrWhiteSpace(cmd.Label)) account.Rename(cmd.Label);

        var server = await db.MailServers.AsTracking().FirstOrDefaultAsync(m => m.AccountId == account.Id, ct);
        if (server is null)
        {
            server = MailServerSettings.Create(me.TenantId, account.Id, me.Id, e, now).Value!;
            db.MailServers.Add(server);
        }
        else
        {
            server.Update(e, now);
        }

        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrEmpty(cmd.Password))
        {
            await ConnectedAccountSecrets.OutsideTransactionAsync(() =>
                secrets.SetAsync(ConnectedAccountSecrets.MailPassword(me.TenantId, account.Id), cmd.Password, null, ct));
        }

        var (tenantId, accountId) = (me.TenantId, account.Id);
        jobs?.Enqueue<SyncConnectedMailboxJob>(job => job.ExecuteAsync(tenantId, accountId));

        return Result.Ok(ListMyConnectedAccountsHandler.ToDto(account, [], server));
    }
}

// ── Turn the mailbox off ─────────────────────────────────────────────────────────────────────

internal sealed record DisableMailboxCommand(Guid AccountId) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ConnectedAccount";
    public string? ResourceId => AccountId.ToString();
}

/// <summary>
/// « Désactiver la messagerie » on a Microsoft or Google account: the CRM stops reading and
/// sending its mail and DELETES what it kept from it; the agendas stay. The permission granted at the provider stays too — only
/// removing the account (or the user, at the provider) withdraws it. An IMAP account is removed.
/// </summary>
internal sealed class DisableMailboxHandler(IntegrationDbContext db, ICurrentUser me) : IRequestHandler<DisableMailboxCommand, Result>
{
    public async Task<Result> Handle(DisableMailboxCommand cmd, CancellationToken ct)
    {
        var account = await db.ConnectedAccounts.AsTracking().FirstOrDefaultAsync(a => a.Id == cmd.AccountId && a.UserId == me.Id, ct);
        if (account is null) return Result.Fail(ConnectedAccountErrors.NotFound);
        if (!account.IsMailEnabled) return Result.Ok();

        var disabled = account.DisableMail();
        if (disabled.IsFailure) return disabled;

        // Nothing is kept of a mailbox the user turned off: what was read from it goes now.
        await ConnectedMailboxSync.ForgetMailboxAsync(db, account.Id, ct);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

// ── Input ────────────────────────────────────────────────────────────────────────────────────

internal static class ImapMailboxInput
{
    public static Result<MailServerEndpoints> Endpoints(ImapMailboxRequest r)
    {
        if (!Enum.TryParse<MailServerSecurity>(r.ImapSecurity, ignoreCase: true, out var imapSecurity)
            || !Enum.TryParse<MailServerSecurity>(r.SmtpSecurity, ignoreCase: true, out var smtpSecurity))
        {
            return Result.Fail<MailServerEndpoints>("MAIL_SECURITY_INVALID");
        }

        return new MailServerEndpoints(r.ImapHost, r.ImapPort, imapSecurity, r.SmtpHost, r.SmtpPort, smtpSecurity, r.Username).Validate();
    }

    /// <summary>The address as typed, trimmed, when it parses as ONE bare address — « Nom &lt;a@b&gt; » is refused.</summary>
    public static string? Email(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 320) return null;
        return MailAddress.TryCreate(trimmed, out var address) && address.Address == trimmed ? trimmed : null;
    }
}

// ── Endpoints ────────────────────────────────────────────────────────────────────────────────

internal static class ConnectedMailboxEndpoints
{
    /// <summary>Inside <c>integration/me/connected-accounts</c>, which already requires ConnectedAccounts.Use.</summary>
    internal static RouteGroupBuilder MapConnectedMailboxEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("imap/test", async (ImapMailboxRequest req, Guid? accountId, ISender sender, CancellationToken ct) =>
                ConnectedAccountEndpoints.Map(await sender.Send(new TestImapMailboxQuery(req, accountId), ct)))
            .WithName("TestImapMailbox")
            .WithSummary("Logs into the IMAP then the SMTP server and says which step failed; saves nothing")
            .WithDescription(
                "Answers 200 with `ok: false` and a code when a server refuses (`MAIL_IMAP_AUTH_FAILED`…), 422 when the "
                + "settings themselves are invalid (port not allowed, bad host). With `accountId` and no password, the "
                + "caller's stored password of that mailbox is used. Limited to 10 tries a minute per user.")
            .RequireRateLimiting("mail-server-probe")
            .Produces<MailServerTestResult>()
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status429TooManyRequests)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("imap", async (ImapMailboxRequest req, ISender sender, CancellationToken ct) =>
                ConnectedAccountEndpoints.Map(await sender.Send(ConnectImapMailboxCommand.From(req), ct)))
            .WithName("ConnectImapMailbox")
            .WithSummary("Connects an IMAP/SMTP mailbox once both servers accept the login; the password goes to the vault")
            .WithDescription(
                "Allowed ports: IMAP 993 or 143, SMTP 465, 587, 25 or 2525; security `Ssl` or `StartTls` (required, "
                + "never optional). A server refusing answers 422 with the probe's code. The same login on the same "
                + "server updates the existing mailbox. Limited to 10 tries a minute per user.")
            .RequireRateLimiting("mail-server-probe")
            .Produces<ConnectedAccountDto>()
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status429TooManyRequests)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPut("{id:guid}/imap", async (Guid id, ImapMailboxRequest req, ISender sender, CancellationToken ct) =>
                ConnectedAccountEndpoints.Map(await sender.Send(UpdateImapMailboxCommand.From(id, req), ct)))
            .WithName("UpdateImapMailbox")
            .WithSummary("Changes an IMAP mailbox's servers, login or password (null keeps the stored one), after a new probe")
            .RequireRateLimiting("mail-server-probe")
            .Produces<ConnectedAccountDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status429TooManyRequests)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapDelete("{id:guid}/mail", async (Guid id, ISender sender, CancellationToken ct) =>
                ConnectedAccountEndpoints.Map(await sender.Send(new DisableMailboxCommand(id), ct)))
            .WithName("DisableMailbox")
            .WithSummary("Stops reading and sending a Microsoft or Google account's mail; its agendas stay")
            .WithDescription(
                "An IMAP mailbox is nothing but its mail: it answers 422 `CONNECTED_MAILBOX_IMAP_REMOVE_INSTEAD` — remove "
                + "the account. Turning the mailbox back on goes through `authorize` with `includeMail`.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return group;
    }
}

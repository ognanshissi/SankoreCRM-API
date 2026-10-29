namespace Sankore.Modules.Integration.Features.ConnectedAccounts;

using System.Text.Json;
using Hangfire;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

// ── DTOs ─────────────────────────────────────────────────────────────────────────────────────

/// <param name="Providers">Those the deployment can connect — a provider with no OAuth client configured is absent.</param>
public sealed record ConnectedAccountsDto(IReadOnlyList<string> Providers, IReadOnlyList<ConnectedAccountDto> Accounts);

/// <param name="Provider"><c>Microsoft</c>, <c>Google</c> or <c>Imap</c>.</param>
/// <param name="Status"><c>Active</c> or <c>NeedsReconnect</c>.</param>
/// <param name="Calendars">Its agendas, ticked or not (lot 2). Empty until its first sync, always empty for an IMAP mailbox.</param>
/// <param name="MailEnabled">The CRM reads and sends this account's e-mail (connected mailbox).</param>
/// <param name="MailServer">The servers of an IMAP mailbox — never its password. Null for Microsoft and Google.</param>
public sealed record ConnectedAccountDto(
    Guid Id,
    string Provider,
    string Label,
    string Email,
    string? DisplayName,
    string Status,
    DateTimeOffset ConnectedAt,
    DateTimeOffset? LastSyncAt,
    string? LastError,
    IReadOnlyList<ConnectedCalendarDto>? Calendars = null,
    bool MailEnabled = false,
    DateTimeOffset? MailEnabledAt = null,
    MailServerDto? MailServer = null,
    DateTimeOffset? MailSyncedAt = null,
    string? MailLastError = null,
    string? MailSignature = null);

/// <param name="IsSubscription">A subscribed agenda (public holidays, someone else's) — shown « abonnement ».</param>
/// <param name="EventCount">Events stored for the window (−30 days, +180 days).</param>
public sealed record ConnectedCalendarDto(
    Guid Id,
    string Name,
    string? Color,
    bool IsPrimary,
    bool IsReadOnly,
    bool IsSubscription,
    bool IsSelected,
    int EventCount,
    DateTimeOffset? LastSyncAt,
    string? LastError,
    bool IsAppointmentTarget = false);

public sealed record SelectConnectedCalendarRequest(bool IsSelected);

/// <summary>One event of the caller's own ticked agendas — title and location in clear, for its owner alone.</summary>
public sealed record MyCalendarEventDto(
    Guid Id,
    Guid AccountId,
    string AccountLabel,
    string Provider,
    Guid CalendarId,
    string CalendarName,
    string? Color,
    string? Title,
    string? Location,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    bool IsAllDay,
    string ShowAs);

/// <param name="IncludeMail">Ask for the mailbox too (read and send).</param>
/// <param name="AccountId">
/// Reconnecting, or turning the mailbox on for, one of the caller's accounts: its address is
/// pre-filled on the provider's screen, and a mailbox already on stays asked for.
/// </param>
public sealed record StartConnectedAccountRequest(string Provider, string? Label, bool IncludeMail = false, Guid? AccountId = null);

public sealed record StartConnectedAccountResult(string AuthorizationUrl);

public sealed record RenameConnectedAccountRequest(string Label);

// ── Return origins ───────────────────────────────────────────────────────────────────────────

/// <summary>
/// Where the browser may be sent back after the consent: the SAME allow-list as CORS
/// (<c>Cors:AllowedOrigins</c>, <c>http://localhost:4222</c> in Development when empty). The
/// callback redirects to an origin recorded when the consent STARTED — from an authenticated call
/// whose <c>Origin</c> the browser set — and never to one read off the callback's own query, which
/// anybody can forge: that would be an open redirect carrying a fresh session's result.
/// </summary>
internal sealed class ConnectedAccountReturnOrigins(IConfiguration config, IHostEnvironment env)
{
    public string? Validate(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return null;

        var allowed = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        if (allowed.Length == 0 && env.IsDevelopment()) allowed = ["http://localhost:4222"];

        var normalized = origin.Trim().TrimEnd('/');
        return allowed.FirstOrDefault(a => string.Equals(a.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase)) is { } match
            ? match.TrimEnd('/')
            : null;
    }
}

// ── Tokens ───────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The access token of a connected account, refreshed when it is about to expire — the one door
/// the calendar and contact syncs of the next lots go through.
///
/// <para>
/// A refresh the provider REFUSES (<c>invalid_grant</c>: revoked, password changed, expired) marks
/// the account « à reconnecter » and stops there: retrying a refused token only fills the
/// provider's logs. An UNAVAILABLE provider leaves the account as it is.
/// </para>
/// </summary>
internal sealed class ConnectedAccountTokens(
    IntegrationDbContext db,
    ISecretsModule secrets,
    ConnectedAccountProviders providers,
    ILogger<ConnectedAccountTokens> logger)
{
    /// <summary>Refreshed this long before it expires, so a token is never handed over with seconds left.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(2);

    public async Task<Result<string>> GetAccessTokenAsync(ConnectedAccount account, CancellationToken ct)
    {
        if (account.Status == ConnectedAccountStatus.NeedsReconnect)
            return Result.Fail<string>(ConnectedAccountErrors.NeedsReconnect);

        var accessKey = ConnectedAccountSecrets.AccessToken(account.TenantId, account.Id);
        var hint = await ConnectedAccountSecrets.OutsideTransactionAsync(() => secrets.GetHintAsync(accessKey, ct));

        if (hint?.ExpiresAt is { } expires && expires - Margin > DateTimeOffset.UtcNow
            && await ConnectedAccountSecrets.OutsideTransactionAsync(() => secrets.GetValueAsync(accessKey, ct)) is { Length: > 0 } cached)
        {
            return Result.Ok(cached);
        }

        var provider = providers.Find(account.Provider);
        if (provider is null) return Result.Fail<string>(ConnectedAccountErrors.ProviderUnavailable);

        var refresh = await ConnectedAccountSecrets.OutsideTransactionAsync(() => secrets.GetValueAsync(ConnectedAccountSecrets.RefreshToken(account.TenantId, account.Id), ct));

        if (string.IsNullOrWhiteSpace(refresh))
        {
            account.MarkNeedsReconnect("refresh_token_missing");
            await db.SaveChangesAsync(ct);
            return Result.Fail<string>(ConnectedAccountErrors.NeedsReconnect);
        }

        try
        {
            var tokens = await provider.RefreshAsync(refresh, account.IsMailEnabled, ct);
            await StoreAsync(account.TenantId, account.Id, tokens, ct);
            return Result.Ok(tokens.AccessToken);
        }
        catch (ProviderAuthException ex) when (ex.Revoked)
        {
            logger.LogInformation("Connected account {AccountId}: the provider refused the refresh ({Code}).", account.Id, ex.Code);
            account.MarkNeedsReconnect(ex.Code);
            await db.SaveChangesAsync(ct);
            return Result.Fail<string>(ConnectedAccountErrors.NeedsReconnect);
        }
        catch (ProviderAuthException ex)
        {
            logger.LogWarning("Connected account {AccountId}: refresh failed ({Code}).", account.Id, ex.Code);
            return Result.Fail<string>(ConnectedAccountErrors.ProviderError);
        }
    }

    /// <summary>A provider that issued no new refresh token keeps the old one — Microsoft rotates, Google usually does not.</summary>
    public Task StoreAsync(Guid tenantId, Guid accountId, ProviderTokens tokens, CancellationToken ct) =>
        ConnectedAccountSecrets.OutsideTransactionAsync(async () =>
        {
            await secrets.SetAsync(ConnectedAccountSecrets.AccessToken(tenantId, accountId), tokens.AccessToken, tokens.ExpiresAt, ct);

            if (!string.IsNullOrWhiteSpace(tokens.RefreshToken))
                await secrets.SetAsync(ConnectedAccountSecrets.RefreshToken(tenantId, accountId), tokens.RefreshToken, null, ct);
        });
}

// ── List ─────────────────────────────────────────────────────────────────────────────────────

internal sealed record ListMyConnectedAccountsQuery : IRequest<ConnectedAccountsDto>;

/// <summary>The caller's own accounts, and nobody else's — there is no listing of a colleague's.</summary>
internal sealed class ListMyConnectedAccountsHandler(IntegrationDbContext db, ICurrentUser me, ConnectedAccountProviders providers)
    : IRequestHandler<ListMyConnectedAccountsQuery, ConnectedAccountsDto>
{
    public async Task<ConnectedAccountsDto> Handle(ListMyConnectedAccountsQuery query, CancellationToken ct)
    {
        var accounts = await db.ConnectedAccounts
            .Where(a => a.UserId == me.Id)
            .OrderBy(a => a.Provider).ThenBy(a => a.ConnectedAt)
            .ToListAsync(ct);

        var calendars = await db.ConnectedCalendars
            .Where(c => c.UserId == me.Id)
            .ToListAsync(ct);

        var servers = await db.MailServers
            .Where(m => m.UserId == me.Id)
            .ToDictionaryAsync(m => m.AccountId, ct);

        return new ConnectedAccountsDto(
            providers.Available().Select(p => p.ToString()).ToList(),
            accounts.Select(a => ToDto(a, calendars.Where(c => c.AccountId == a.Id), servers.GetValueOrDefault(a.Id))).ToList());
    }

    internal static ConnectedAccountDto ToDto(ConnectedAccount a, IEnumerable<ConnectedCalendar> calendars, MailServerSettings? server = null) => new(
        a.Id, a.Provider.ToString(), a.Label, a.Email, a.DisplayName, a.Status.ToString(), a.ConnectedAt, a.LastSyncAt, a.LastError,
        calendars
            .OrderByDescending(c => c.IsPrimary).ThenBy(c => c.IsSubscription).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new ConnectedCalendarDto(c.Id, c.Name, c.Color, c.IsPrimary, c.IsReadOnly, c.IsSubscription, c.IsSelected,
                c.EventCount, c.LastSyncAt, c.LastError, c.IsAppointmentTarget))
            .ToList(),
        a.IsMailEnabled,
        a.MailEnabledAt,
        server is null ? null : MailServerDto.From(server),
        a.MailSyncedAt,
        a.MailLastError,
        a.MailSignature);
}

// ── Start a consent ──────────────────────────────────────────────────────────────────────────

internal sealed record StartConnectedAccountCommand(
    string Provider, string? Label, string? Origin, string? RequestBaseUrl, bool IncludeMail = false, Guid? AccountId = null)
    : IRequest<Result<StartConnectedAccountResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "ConnectedAccount";
    public string? ResourceId => Provider;
}

/// <summary>
/// Opens a consent: a fresh state, its hashed row, and the provider's authorization URL with the
/// PKCE challenge. The browser then navigates there; the callback below finishes the job.
/// </summary>
internal sealed class StartConnectedAccountHandler(
    IntegrationDbContext db,
    ICurrentUser me,
    ConnectedAccountProviders providers,
    ConnectedAccountOAuth oauth,
    ConnectedAccountReturnOrigins origins,
    IOptions<ConnectedAccountOptions> options,
    TimeProvider clock)
    : IRequestHandler<StartConnectedAccountCommand, Result<StartConnectedAccountResult>>
{
    public async Task<Result<StartConnectedAccountResult>> Handle(StartConnectedAccountCommand cmd, CancellationToken ct)
    {
        if (!Enum.TryParse<ConnectedAccountProvider>(cmd.Provider, ignoreCase: true, out var kind)
            || providers.Find(kind) is not { } provider)
        {
            return Result.Fail<StartConnectedAccountResult>(ConnectedAccountErrors.ProviderUnavailable);
        }

        var origin = origins.Validate(cmd.Origin);
        if (origin is null) return Result.Fail<StartConnectedAccountResult>(ConnectedAccountErrors.OriginNotAllowed);

        var redirectUri = ConnectedAccountCallback.RedirectUri(options.Value, cmd.RequestBaseUrl, kind);
        if (redirectUri is null) return Result.Fail<StartConnectedAccountResult>(ConnectedAccountErrors.ProviderUnavailable);

        var consent = new ProviderConsent(cmd.IncludeMail);

        if (cmd.AccountId is { } accountId)
        {
            var existing = await db.ConnectedAccounts.FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == me.Id && a.Provider == kind, ct);
            if (existing is null) return Result.Fail<StartConnectedAccountResult>(ConnectedAccountErrors.NotFound);

            // A reconnection must not quietly drop a mailbox that was on: the consent asks for it again.
            consent = new ProviderConsent(cmd.IncludeMail || existing.IsMailEnabled, existing.Email);
        }

        var state = ConnectedAccountOAuth.NewState();
        var now = clock.GetUtcNow();

        db.ConnectedAccountOAuthStates.Add(ConnectedAccountOAuthState.Start(
            me.TenantId, me.Id, kind, ConnectedAccountOAuth.HashState(state), cmd.Label, origin, now,
            TimeSpan.FromMinutes(Math.Clamp(options.Value.StateLifetimeMinutes, 2, 30)), consent.IncludeMail));

        await db.SaveChangesAsync(ct);

        var challenge = ConnectedAccountOAuth.CodeChallenge(oauth.CodeVerifier(state));
        return Result.Ok(new StartConnectedAccountResult(provider.BuildAuthorizationUrl(state, challenge, redirectUri, consent).ToString()));
    }
}

// ── Rename, test, remove ─────────────────────────────────────────────────────────────────────

internal sealed record RenameConnectedAccountCommand(Guid AccountId, string Label) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ConnectedAccount";
    public string? ResourceId => AccountId.ToString();
}

internal sealed class RenameConnectedAccountHandler(IntegrationDbContext db, ICurrentUser me)
    : IRequestHandler<RenameConnectedAccountCommand, Result>
{
    public async Task<Result> Handle(RenameConnectedAccountCommand cmd, CancellationToken ct)
    {
        var account = await db.ConnectedAccounts.AsTracking().FirstOrDefaultAsync(a => a.Id == cmd.AccountId && a.UserId == me.Id, ct);
        if (account is null) return Result.Fail(ConnectedAccountErrors.NotFound);

        var renamed = account.Rename(cmd.Label);
        if (renamed.IsFailure) return renamed;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

internal sealed record SyncConnectedAccountCommand(Guid AccountId) : IRequest<Result<ConnectedAccountDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "ConnectedAccount";
    public string? ResourceId => AccountId.ToString();
}

/// <summary>
/// « Synchroniser »: the account's agendas read now (lot 2) — its list, then each ticked agenda's
/// changes. The 15-minute job does the same in the background.
/// </summary>
internal sealed class SyncConnectedAccountHandler(IntegrationDbContext db, ICurrentUser me, ConnectedCalendarSync sync)
    : IRequestHandler<SyncConnectedAccountCommand, Result<ConnectedAccountDto>>
{
    public async Task<Result<ConnectedAccountDto>> Handle(SyncConnectedAccountCommand cmd, CancellationToken ct)
    {
        var account = await db.ConnectedAccounts.AsTracking().FirstOrDefaultAsync(a => a.Id == cmd.AccountId && a.UserId == me.Id, ct);
        if (account is null) return Result.Fail<ConnectedAccountDto>(ConnectedAccountErrors.NotFound);

        var result = await sync.SyncAccountAsync(account, ct);
        if (result.IsFailure) return Result.Fail<ConnectedAccountDto>(result.Error!);

        var calendars = await db.ConnectedCalendars.Where(c => c.AccountId == account.Id).ToListAsync(ct);
        var server = await db.MailServers.FirstOrDefaultAsync(m => m.AccountId == account.Id, ct);
        return Result.Ok(ListMyConnectedAccountsHandler.ToDto(account, calendars, server));
    }
}

// ── Tick an agenda ───────────────────────────────────────────────────────────────────────────

internal sealed record SelectConnectedCalendarCommand(Guid AccountId, Guid CalendarId, bool IsSelected)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ConnectedCalendar";
    public string? ResourceId => CalendarId.ToString();
}

/// <summary>
/// Ticks or unticks an agenda (lot 2). Unticking DELETES what was read from it — nothing is kept
/// that the user chose not to show; ticking reads it in the background, out of the request's way.
/// </summary>
internal sealed class SelectConnectedCalendarHandler(
    IntegrationDbContext db, ICurrentUser me, ConnectedCalendarSync sync, IBackgroundJobClient jobs)
    : IRequestHandler<SelectConnectedCalendarCommand, Result>
{
    public async Task<Result> Handle(SelectConnectedCalendarCommand cmd, CancellationToken ct)
    {
        var calendar = await db.ConnectedCalendars.AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.CalendarId && c.AccountId == cmd.AccountId && c.UserId == me.Id, ct);

        if (calendar is null) return Result.Fail(ConnectedAccountErrors.NotFound);
        if (calendar.IsSelected == cmd.IsSelected) return Result.Ok();

        calendar.Select(cmd.IsSelected);
        if (!cmd.IsSelected) await sync.ForgetEventsAsync([calendar.Id], ct);

        await db.SaveChangesAsync(ct);

        if (cmd.IsSelected)
        {
            var (tenantId, accountId) = (me.TenantId, cmd.AccountId);
            jobs.Enqueue<SyncConnectedAccountJob>(job => job.ExecuteAsync(tenantId, accountId));
        }

        return Result.Ok();
    }
}

// ── My events ────────────────────────────────────────────────────────────────────────────────

internal sealed record ListMyCalendarEventsQuery(DateTimeOffset From, DateTimeOffset To) : IRequest<Result<IReadOnlyList<MyCalendarEventDto>>>;

/// <summary>
/// The caller's OWN events, decrypted, between two instants (62 days at most) — what the calendar
/// screen of lot 3 lays out. Never another user's: a colleague will see busy slots only (lot 6).
/// </summary>
internal sealed class ListMyCalendarEventsHandler(IntegrationDbContext db, ICurrentUser me, CalendarEventProtector protector)
    : IRequestHandler<ListMyCalendarEventsQuery, Result<IReadOnlyList<MyCalendarEventDto>>>
{
    public async Task<Result<IReadOnlyList<MyCalendarEventDto>>> Handle(ListMyCalendarEventsQuery query, CancellationToken ct)
    {
        if (query.To <= query.From || query.To - query.From > TimeSpan.FromDays(62))
            return Result.Fail<IReadOnlyList<MyCalendarEventDto>>("CALENDAR_RANGE_INVALID");

        var rows = await (
                from e in db.ConnectedCalendarEvents
                join c in db.ConnectedCalendars on e.CalendarId equals c.Id
                join a in db.ConnectedAccounts on e.AccountId equals a.Id
                where e.UserId == me.Id && c.IsSelected && e.StartAt < query.To && e.EndAt > query.From
                      // A rendez-vous copied there by lot 4 comes back on the next read: the screen
                      // already shows the original, and the copy would conflict with it.
                      && !db.AppointmentCalendarCopies.Any(cp => cp.CalendarId == e.CalendarId && cp.ExternalEventId == e.ExternalEventId)
                orderby e.StartAt
                select new { Event = e, CalendarName = c.Name, c.Color, AccountLabel = a.Label, a.Provider })
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<MyCalendarEventDto>>(rows.Select(r => new MyCalendarEventDto(
            r.Event.Id, r.Event.AccountId, r.AccountLabel, r.Provider.ToString(), r.Event.CalendarId, r.CalendarName, r.Color,
            protector.Reveal(r.Event.TitleEnc), protector.Reveal(r.Event.LocationEnc),
            r.Event.StartAt, r.Event.EndAt, r.Event.IsAllDay, r.Event.ShowAs.ToString())).ToList());
    }
}

internal sealed record RemoveConnectedAccountCommand(Guid AccountId) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ConnectedAccount";
    public string? ResourceId => AccountId.ToString();
}

/// <summary>
/// « Retirer »: the provider is asked to revoke (Google; Microsoft has no per-application
/// revocation), then the tokens and the row go. What later lots import from the account — agenda
/// events, contacts — is removed with it.
/// </summary>
internal sealed class RemoveConnectedAccountHandler(
    IntegrationDbContext db, ICurrentUser me, ISecretsModule secrets, ConnectedAccountProviders providers,
    AppointmentCalendarCopies copies, ILogger<RemoveConnectedAccountHandler> logger)
    : IRequestHandler<RemoveConnectedAccountCommand, Result>
{
    public async Task<Result> Handle(RemoveConnectedAccountCommand cmd, CancellationToken ct)
    {
        var account = await db.ConnectedAccounts.AsTracking().FirstOrDefaultAsync(a => a.Id == cmd.AccountId && a.UserId == me.Id, ct);
        if (account is null) return Result.Fail(ConnectedAccountErrors.NotFound);

        // The rendez-vous copied into it first, while its token still works (lot 4).
        await copies.ForgetAccountAsync(account, ct);

        var refresh = await ConnectedAccountSecrets.OutsideTransactionAsync(() => secrets.GetValueAsync(ConnectedAccountSecrets.RefreshToken(account.TenantId, account.Id), ct));

        if (refresh is { Length: > 0 } && providers.Find(account.Provider) is { } provider)
        {
            try
            {
                await provider.RevokeAsync(refresh, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Connected account {AccountId}: the provider revocation failed ({Reason}).", account.Id, ex.GetType().Name);
            }
        }

        // What the account brought goes with it: its agendas and their events.
        var calendars = await db.ConnectedCalendars.AsTracking().Where(c => c.AccountId == account.Id).ToListAsync(ct);
        var calendarIds = calendars.Select(c => c.Id).ToList();
        db.ConnectedCalendarEvents.RemoveRange(
            await db.ConnectedCalendarEvents.AsTracking().Where(e => calendarIds.Contains(e.CalendarId)).ToListAsync(ct));
        db.ConnectedCalendars.RemoveRange(calendars);

        if (await db.MailServers.AsTracking().FirstOrDefaultAsync(m => m.AccountId == account.Id, ct) is { } server)
            db.MailServers.Remove(server);

        // The e-mails kept from it (connected mailbox, lot 2).
        await ConnectedMailboxSync.ForgetMailboxAsync(db, account.Id, ct);

        db.ConnectedAccounts.Remove(account);
        await db.SaveChangesAsync(ct);

        // After the row: a secret left behind is unreachable, a row left without its secret would
        // look connected and fail on every use.
        await ConnectedAccountSecrets.OutsideTransactionAsync(() => secrets.DeleteAllAsync(account.TenantId, ConnectedAccountSecrets.Scope, account.Id, ct));
        return Result.Ok();
    }
}

// ── The provider's callback ──────────────────────────────────────────────────────────────────

/// <summary>
/// <c>GET /integration/oauth/{provider}/callback</c> — where Microsoft or Google send the browser
/// back with a code. Anonymous by nature (a top-level navigation from the provider's domain carries
/// no JWT): who asked is read off the single-use state row, and the tenant with it.
///
/// <para>
/// <b>Always a redirect back to the front</b>, never a page of its own: to
/// <c>{origin}/settings/account/agendas?connexion=ok|refuse|expire|erreur</c>, plus
/// <c>&amp;messagerie=ok|refusee</c> when the consent asked for the mailbox. A state nobody
/// knows gets a bare 400 — there is no origin to send that browser to, and inventing one would be
/// the open redirect the recorded origin exists to prevent.
/// </para>
/// </summary>
internal static class ConnectedAccountCallback
{
    public const string ReturnPath = "/settings/account/agendas";

    public static Uri? RedirectUri(ConnectedAccountOptions options, string? requestBaseUrl, ConnectedAccountProvider provider)
    {
        var baseUrl = options.PublicBaseUrl is { Length: > 0 } configured ? configured : requestBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        return Uri.TryCreate(baseUrl.TrimEnd('/') + ConnectedAccountOAuth.CallbackPath(provider), UriKind.Absolute, out var uri) ? uri : null;
    }

    public static async Task<IResult> HandleAsync(
        string provider,
        HttpContext http,
        IntegrationDbContext db,
        ConnectedAccountProviders providers,
        ConnectedAccountOAuth oauth,
        ConnectedAccountTokens tokens,
        IAuditWriter audit,
        IOptions<ConnectedAccountOptions> options,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var logger = loggers.CreateLogger("ConnectedAccountCallback");
        var query = http.Request.Query;
        var state = query["state"].ToString();

        if (!Enum.TryParse<ConnectedAccountProvider>(provider, ignoreCase: true, out var kind) || string.IsNullOrWhiteSpace(state))
            return Results.BadRequest("Lien de connexion invalide.");

        var hash = ConnectedAccountOAuth.HashState(state);
        var row = await db.ConnectedAccountOAuthStates
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(s => s.StateHash == hash && s.Provider == kind, ct);

        if (row is null) return Results.BadRequest("Lien de connexion invalide ou expiré.");

        var now = clock.GetUtcNow();
        IResult Back(string outcome, Guid? accountId = null, bool? mailGranted = null) =>
            Results.Redirect($"{row.ReturnOrigin}{ReturnPath}?connexion={outcome}"
                + (accountId is { } id ? $"&compte={id}" : string.Empty)
                + (mailGranted is { } mail ? $"&messagerie={(mail ? "ok" : "refusee")}" : string.Empty));

        // Consumed BEFORE anything is attempted: a state is good for one try, success or not.
        if (!row.TryConsume(now)) return Back("expire");
        await db.SaveChangesAsync(ct);

        if (query.ContainsKey("error")) return Back("refuse");

        var code = query["code"].ToString();
        var implementation = providers.Find(kind);
        var redirectUri = RedirectUri(options.Value, $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}", kind);

        if (string.IsNullOrWhiteSpace(code) || implementation is null || redirectUri is null) return Back("erreur");

        ProviderTokens issued;
        ProviderProfile profile;

        try
        {
            issued = await implementation.ExchangeCodeAsync(code, oauth.CodeVerifier(state), redirectUri, row.IncludeMail, ct);
            profile = await implementation.GetProfileAsync(issued.AccessToken, ct);
        }
        catch (ProviderAuthException ex)
        {
            logger.LogWarning("Connected account consent of user {UserId} failed at {Provider}: {Code}.", row.UserId, kind, ex.Code);
            return Back("erreur");
        }

        var account = await db.ConnectedAccounts
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(a => a.TenantId == row.TenantId && a.UserId == row.UserId
                                   && a.Provider == kind && a.ExternalAccountId == profile.ExternalId, ct);

        var isNew = account is null;

        if (isNew && string.IsNullOrWhiteSpace(issued.RefreshToken))
        {
            // Without a refresh token the account dies within the hour: refuse it now rather than
            // show a « Synchronisé » that turns into « À reconnecter » after lunch.
            logger.LogWarning("Connected account consent of user {UserId} at {Provider}: no refresh token issued.", row.UserId, kind);
            return Back("erreur");
        }

        if (account is null)
        {
            account = ConnectedAccount.Connect(row.TenantId, row.UserId, kind, profile.ExternalId, profile.Email,
                profile.DisplayName, row.Label, issued.Scopes, now);
            db.ConnectedAccounts.Add(account);
        }
        else
        {
            account.Reconnect(profile.Email, profile.DisplayName, row.Label, issued.Scopes, now);
        }

        // Only a consent that ASKED for the mailbox decides it: Google returns every scope ever
        // granted, so a mere reconnection would otherwise turn back on a mailbox the user turned off.
        bool? mailGranted = null;
        if (row.IncludeMail)
        {
            mailGranted = implementation.GrantsMail(issued.Scopes);
            account.ApplyMailGrant(mailGranted.Value, now);
        }

        account.MarkSynced(now);
        await db.SaveChangesAsync(ct);
        await tokens.StoreAsync(row.TenantId, account.Id, issued, ct);

        // Its agendas are read at once, in the background: the screen the user returns to shows
        // the account straight away, and its agendas a few seconds later.
        var (tenantId, accountId) = (row.TenantId, account.Id);
        http.RequestServices.GetService<IBackgroundJobClient>()?.Enqueue<SyncConnectedAccountJob>(job => job.ExecuteAsync(tenantId, accountId));
        if (account.IsMailEnabled)
            http.RequestServices.GetService<IBackgroundJobClient>()?.Enqueue<SyncConnectedMailboxJob>(job => job.ExecuteAsync(tenantId, accountId));

        // An anonymous request, so no AuditBehavior: the row is written by hand, with the user the
        // state names. The address is the account's own, which its owner just typed in.
        try
        {
            await audit.WriteAsync(new AuditEntry(
                now, row.UserId, row.TenantId, isNew ? "ConnectedAccountConnected" : "ConnectedAccountReconnected",
                JsonSerializer.Serialize(new { provider = kind.ToString(), account.Email, mail = mailGranted }),
                "SUCCESS", null, "ConnectedAccount", account.Id.ToString()), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Audit of connected account {AccountId} could not be written.", account.Id);
        }

        return Back("ok", account.Id, mailGranted);
    }
}

// ── Endpoints ────────────────────────────────────────────────────────────────────────────────

internal static class ConnectedAccountEndpoints
{
    /// <summary>Under <c>api/v1</c>: the caller's own accounts.</summary>
    internal static IEndpointRouteBuilder MapConnectedAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("integration/me/connected-accounts")
            .WithTags("ConnectedAccounts")
            .RequireAuthorization(Permissions.CanUseConnectedAccounts.Code);

        group.MapGet("", async (ISender sender, CancellationToken ct) => Results.Ok(await sender.Send(new ListMyConnectedAccountsQuery(), ct)))
            .WithName("ListMyConnectedAccounts")
            .WithSummary("The caller's connected Microsoft and Google accounts, and the providers this deployment can connect")
            .Produces<ConnectedAccountsDto>()
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("authorize", async (StartConnectedAccountRequest req, HttpContext http, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new StartConnectedAccountCommand(
                    req.Provider, req.Label, http.Request.Headers.Origin.ToString(),
                    $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}", req.IncludeMail, req.AccountId), ct)))
            .WithName("StartConnectedAccount")
            .WithSummary("Starts connecting a Microsoft or Google account: answers the provider's consent URL to navigate to")
            .WithDescription(
                "The browser navigates to `authorizationUrl`; the provider sends it back to "
                + "`/integration/oauth/{provider}/callback`, which redirects to "
                + "`{origin}/settings/account/agendas?connexion=ok|refuse|expire|erreur` (plus `&messagerie=ok|refusee` "
                + "when `includeMail`). `accountId` reconnects one of the caller's accounts, keeping its mailbox if it is on. "
                + "The origin is the calling page's, checked against the CORS allow-list. Requires permission: ConnectedAccounts.Use.")
            .Produces<StartConnectedAccountResult>()
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPatch("{id:guid}", async (Guid id, RenameConnectedAccountRequest req, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new RenameConnectedAccountCommand(id, req.Label), ct)))
            .WithName("RenameConnectedAccount")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{id:guid}/sync", async (Guid id, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new SyncConnectedAccountCommand(id), ct)))
            .WithName("SyncConnectedAccount")
            .WithSummary("Checks the account still answers and records the synchronisation")
            .Produces<ConnectedAccountDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPut("{id:guid}/calendars/{calendarId:guid}",
                async (Guid id, Guid calendarId, SelectConnectedCalendarRequest req, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new SelectConnectedCalendarCommand(id, calendarId, req.IsSelected), ct)))
            .WithName("SelectConnectedCalendar")
            .WithSummary("Ticks or unticks an agenda: unticking deletes what was read from it, ticking reads it in the background")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapGet("integration/me/calendar-events",
                async (DateTimeOffset from, DateTimeOffset to, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new ListMyCalendarEventsQuery(from, to), ct)))
            .WithTags("ConnectedAccounts")
            .WithName("ListMyCalendarEvents")
            .WithSummary("The caller's own events from their ticked agendas, between two instants (62 days at most)")
            .RequireAuthorization(Permissions.CanUseConnectedAccounts.Code)
            .Produces<IReadOnlyList<MyCalendarEventDto>>()
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPut("appointment-target", async (SetAppointmentTargetRequest req, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new SetAppointmentTargetCommand(req.CalendarId), ct)))
            .WithName("SetAppointmentTarget")
            .WithSummary("Chooses the agenda the caller's SANKORÉ rendez-vous are copied into, or none")
            .WithDescription(
                "One writable agenda of the caller's own accounts, or `calendarId: null` to stop copying. "
                + "Upcoming rendez-vous move to the new agenda in the background. Requires permission: ConnectedAccounts.Use.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapDelete("{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new RemoveConnectedAccountCommand(id), ct)))
            .WithName("RemoveConnectedAccount")
            .WithSummary("Disconnects the account: revokes at the provider when it can, deletes the tokens and the account")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapConnectedMailboxEndpoints();
        app.MapConnectedMailboxReadEndpoints();
        app.MapConnectedMailboxSendEndpoints();
        app.MapConnectedMailboxScheduledEndpoints();

        return app;
    }

    /// <summary>On the root app, outside <c>api/v1</c> and exempt from host tenant resolution.</summary>
    internal static IEndpointRouteBuilder MapConnectedAccountOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/integration/oauth/{provider}/callback", ConnectedAccountCallback.HandleAsync)
            .AllowAnonymous()
            .RequireRateLimiting("oauth-callback")
            .ExcludeFromDescription();

        return app;
    }

    internal static IResult Map<T>(Result<T> result) => result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);

    internal static IResult Map(Result result) => result.IsSuccess ? Results.NoContent() : Problem(result.Error!);

    private static IResult Problem(string code) => code switch
    {
        ConnectedAccountErrors.NotFound => Results.Problem(title: "Compte connecté", detail: code, statusCode: StatusCodes.Status404NotFound),
        ConnectedAccountErrors.NeedsReconnect => Results.Problem(title: "Compte connecté", detail: code, statusCode: StatusCodes.Status409Conflict),
        ConnectedAccountErrors.ProviderError => Results.Problem(title: "Compte connecté", detail: code, statusCode: StatusCodes.Status502BadGateway),
        _ => Results.Problem(title: "Compte connecté", detail: code, statusCode: StatusCodes.Status422UnprocessableEntity),
    };
}

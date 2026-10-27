namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>The providers a user may connect an account of (agendas, contacts, mailbox).</summary>
public enum ConnectedAccountProvider
{
    /// <summary>Microsoft 365, Outlook.com, Exchange — through Microsoft Graph.</summary>
    Microsoft,

    /// <summary>Google Agenda and Contacts — Gmail and Google Workspace accounts.</summary>
    Google,

    /// <summary>
    /// Any other mailbox, through IMAP and SMTP with a password (connected mailbox, lot 1). A
    /// mailbox only: no agenda, no contacts, no OAuth — its settings are a
    /// <see cref="MailServerSettings"/> row and its password is in the vault.
    /// </summary>
    Imap,
}

public enum ConnectedAccountStatus
{
    /// <summary>The tokens work.</summary>
    Active,

    /// <summary>
    /// The provider refused the refresh token — revoked by the user, a password changed, an
    /// administrator's policy. Nothing syncs until the user connects it again.
    /// </summary>
    NeedsReconnect,
}

/// <summary>
/// A Microsoft or Google account a USER connected to SANKORÉ, to see its agendas and import its
/// contacts (connected agendas, lot 1).
///
/// <para>
/// <b>Not an <see cref="IntegrationConnection"/>, deliberately.</b> A connection belongs to the
/// institution, is configured by an administrator and serves everybody; this belongs to one
/// person, is created by that person through the provider's consent screen, and nobody else reads
/// what it brings. Folding it into the connection socle would put a user's personal Gmail next to
/// the core banking system under <c>Integration.Connection.Manage</c>.
/// </para>
///
/// <para>
/// <b>The tokens are not here.</b> The refresh and access tokens live in the secrets vault, keyed
/// by this row's id (<c>ConnectedAccountSecrets</c>): this table holds who and what, never a
/// credential.
/// </para>
/// </summary>
public sealed class ConnectedAccount : AggregateRoot
{
    public const int LabelMaxLength = 60;

    private ConnectedAccount() { }

    public Guid Id { get; private set; }

    /// <summary>The CRM user who connected it — the only person who sees what it brings.</summary>
    public Guid UserId { get; private set; }

    public ConnectedAccountProvider Provider { get; private set; }

    /// <summary>The provider's stable id of the account (Graph <c>id</c>, Google <c>sub</c>). An e-mail can change, this cannot.</summary>
    public string ExternalAccountId { get; private set; } = default!;

    public string Email { get; private set; } = default!;

    public string? DisplayName { get; private set; }

    /// <summary>What the user calls it — « Travail », « Personnel », « Association ».</summary>
    public string Label { get; private set; } = default!;

    public ConnectedAccountStatus Status { get; private set; }

    /// <summary>The scopes the user actually granted — a user may untick some on the consent screen.</summary>
    public string GrantedScopes { get; private set; } = string.Empty;

    public DateTimeOffset ConnectedAt { get; private set; }

    public DateTimeOffset? LastSyncAt { get; private set; }

    /// <summary>A code, never the provider's text: <c>invalid_grant</c>, <c>consent_revoked</c>…</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// The CRM reads and sends this account's e-mail (connected mailbox, owner's decision
    /// 2026-10-27). For Microsoft and Google it follows what the provider GRANTED — a user may
    /// untick the mail permission on the consent screen — and the user may turn it off without
    /// disconnecting the agendas. Always true for an <see cref="ConnectedAccountProvider.Imap"/> account.
    /// </summary>
    public bool IsMailEnabled { get; private set; }

    public DateTimeOffset? MailEnabledAt { get; private set; }

    /// <summary>The last mailbox sync that finished (lot 2) — distinct from <see cref="LastSyncAt"/>, the agendas'.</summary>
    public DateTimeOffset? MailSyncedAt { get; private set; }

    /// <summary>A code, never the provider's text.</summary>
    public string? MailLastError { get; private set; }

    /// <summary>PostgreSQL xmin.</summary>
    public uint Version { get; private set; }

    /// <summary>An IMAP mailbox has no agenda: the agenda sync and the rendez-vous copies skip it.</summary>
    public bool HasCalendars => Provider != ConnectedAccountProvider.Imap;

    public static ConnectedAccount Connect(
        Guid tenantId,
        Guid userId,
        ConnectedAccountProvider provider,
        string externalAccountId,
        string email,
        string? displayName,
        string? label,
        string grantedScopes,
        DateTimeOffset now)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (userId == Guid.Empty) throw new DomainException("A connected account belongs to a user.");
        if (string.IsNullOrWhiteSpace(externalAccountId)) throw new DomainException("The provider's account id is required.");
        if (string.IsNullOrWhiteSpace(email)) throw new DomainException("The account's e-mail is required.");

        return new ConnectedAccount
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Provider = provider,
            ExternalAccountId = externalAccountId.Trim(),
            Email = email.Trim(),
            DisplayName = Clip(displayName, 200),
            Label = DefaultLabel(label, email),
            Status = ConnectedAccountStatus.Active,
            GrantedScopes = grantedScopes ?? string.Empty,
            ConnectedAt = now,
        };
    }

    /// <summary>The same account connected again — after a revocation, or to grant a scope.</summary>
    public void Reconnect(string email, string? displayName, string? label, string grantedScopes, DateTimeOffset now)
    {
        Email = email.Trim();
        DisplayName = Clip(displayName, 200) ?? DisplayName;
        if (!string.IsNullOrWhiteSpace(label)) Label = DefaultLabel(label, email);
        GrantedScopes = grantedScopes ?? GrantedScopes;
        Status = ConnectedAccountStatus.Active;
        LastError = null;
        ConnectedAt = now;
    }

    /// <summary>
    /// A mailbox reached through IMAP/SMTP. Its stable id is the login on its server — the same
    /// login on the same server connected again is the same account, updated.
    /// </summary>
    public static ConnectedAccount ConnectImap(
        Guid tenantId, Guid userId, string email, string? displayName, string? label, string username, string imapHost, DateTimeOffset now)
    {
        var account = Connect(tenantId, userId, ConnectedAccountProvider.Imap, ImapExternalId(username, imapHost),
            email, displayName, label, "imap smtp", now);
        account.IsMailEnabled = true;
        account.MailEnabledAt = now;
        return account;
    }

    public static string ImapExternalId(string username, string imapHost) =>
        $"{username.Trim()}@{imapHost.Trim().TrimEnd('.')}".ToLowerInvariant();

    /// <summary>
    /// What a consent says about the mailbox: granted turns it on (or keeps it on), refused turns
    /// it off — the provider's word wins over what the account believed.
    /// </summary>
    public void ApplyMailGrant(bool granted, DateTimeOffset now)
    {
        if (granted && !IsMailEnabled) MailEnabledAt = now;
        if (!granted) MailEnabledAt = null;
        IsMailEnabled = granted;
    }

    /// <summary>Stops reading and sending its e-mail; the agendas stay. Not for an IMAP account, which is nothing else.</summary>
    public Result DisableMail()
    {
        if (Provider == ConnectedAccountProvider.Imap) return Result.Fail("CONNECTED_MAILBOX_IMAP_REMOVE_INSTEAD");
        IsMailEnabled = false;
        MailEnabledAt = null;
        return Result.Ok();
    }

    public void RecordMailSync(DateTimeOffset now)
    {
        MailSyncedAt = now;
        MailLastError = null;
    }

    public void RecordMailFailure(string code) => MailLastError = Clip(code, 60);

    /// <summary>The IMAP settings changed: the address or login may have too.</summary>
    public void UpdateImapIdentity(string email, string? displayName, string username, string imapHost, DateTimeOffset now)
    {
        Email = email.Trim();
        DisplayName = Clip(displayName, 200) ?? DisplayName;
        ExternalAccountId = ImapExternalId(username, imapHost);
        Status = ConnectedAccountStatus.Active;
        LastError = null;
        ConnectedAt = now;
    }

    public Result Rename(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return Result.Fail("CONNECTED_ACCOUNT_LABEL_REQUIRED");
        Label = DefaultLabel(label, Email);
        return Result.Ok();
    }

    public void MarkNeedsReconnect(string reason)
    {
        Status = ConnectedAccountStatus.NeedsReconnect;
        LastError = Clip(reason, 60);
    }

    public void MarkSynced(DateTimeOffset now)
    {
        Status = ConnectedAccountStatus.Active;
        LastSyncAt = now;
        LastError = null;
    }

    private static string DefaultLabel(string? label, string email)
    {
        var chosen = Clip(label, LabelMaxLength);
        if (chosen is not null) return chosen;

        // An account nobody named is called by its address, which is what the user would type.
        var address = email.Trim();
        return address.Length <= LabelMaxLength ? address : address[..LabelMaxLength];
    }

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

/// <summary>
/// One consent in progress: the round trip to Microsoft's or Google's consent screen and back.
///
/// <para>
/// <b>Why a row and not a cookie.</b> The callback is a top-level navigation from the provider's
/// domain: it carries no JWT, so it cannot know who asked. The row is what ties the <c>state</c>
/// the provider hands back to a tenant, a user and the front origin to return to. It is
/// single-use (<see cref="ConsumedAt"/>) and short-lived; only a HASH of the state is stored, so
/// a leaked table does not let anybody complete somebody else's consent.
/// </para>
///
/// <para>
/// <b>The PKCE verifier is not stored either</b>: it is derived from the state by an HMAC under a
/// server key (<c>ConnectedAccountOAuth.CodeVerifier</c>), so the row holds nothing that would let
/// a reader redeem an intercepted authorization code.
/// </para>
/// </summary>
public sealed class ConnectedAccountOAuthState : AggregateRoot
{
    private ConnectedAccountOAuthState() { }

    public Guid Id { get; private set; }

    /// <summary>SHA-256 of the state, hex. Globally unique: the callback finds the row with it before it knows the tenant.</summary>
    public string StateHash { get; private set; } = default!;

    public Guid UserId { get; private set; }

    public ConnectedAccountProvider Provider { get; private set; }

    public string? Label { get; private set; }

    /// <summary>The front origin the browser goes back to — validated against the CORS allow-list when the consent started.</summary>
    public string ReturnOrigin { get; private set; } = default!;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    /// <summary>The consent asked for the mailbox too — the token requests must name the same scopes.</summary>
    public bool IncludeMail { get; private set; }

    public static ConnectedAccountOAuthState Start(
        Guid tenantId, Guid userId, ConnectedAccountProvider provider, string stateHash, string? label,
        string returnOrigin, DateTimeOffset now, TimeSpan lifetime, bool includeMail = false) => new()
    {
        IncludeMail = includeMail,
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        UserId = userId,
        Provider = provider,
        StateHash = stateHash,
        Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim()[..Math.Min(label.Trim().Length, ConnectedAccount.LabelMaxLength)],
        ReturnOrigin = returnOrigin,
        CreatedAt = now,
        ExpiresAt = now + lifetime,
    };

    /// <summary>Single use: false when already consumed or expired.</summary>
    public bool TryConsume(DateTimeOffset now)
    {
        if (ConsumedAt is not null || now >= ExpiresAt) return false;
        ConsumedAt = now;
        return true;
    }
}

/// <summary>How an event occupies its slot, as the provider says — what a colleague's view shows (lot 6).</summary>
public enum CalendarShowAs
{
    Busy,
    Free,
    Tentative,
    OutOfOffice,
    WorkingElsewhere,
}

/// <summary>
/// One agenda of a connected account (connected agendas, lot 2) — « Calendrier », « Réunions
/// d'agence », « Famille », a subscribed « Jours fériés ». The user ticks which ones SANKORÉ shows;
/// only ticked agendas are read, and unticking one deletes what was read from it.
/// </summary>
public sealed class ConnectedCalendar : AggregateRoot
{
    /// <summary>How far back and ahead an agenda is read: the calendar screen's horizon.</summary>
    public static readonly TimeSpan WindowBack = TimeSpan.FromDays(30);
    public static readonly TimeSpan WindowAhead = TimeSpan.FromDays(180);

    /// <summary>
    /// A delta cursor is tied to the window it was opened with; past this age the window has
    /// drifted far enough that the next run rebuilds it from scratch.
    /// </summary>
    public static readonly TimeSpan WindowMaxAge = TimeSpan.FromDays(7);

    private ConnectedCalendar() { }

    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid UserId { get; private set; }
    public string ExternalCalendarId { get; private set; } = default!;
    public string Name { get; private set; } = default!;

    /// <summary>« #RRGGBB » as the provider shows it, or null.</summary>
    public string? Color { get; private set; }

    public bool IsPrimary { get; private set; }

    /// <summary>The user cannot write to it — a shared or subscribed agenda. Never a copy target (lot 4).</summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>A subscription (public holidays, someone else's agenda) rather than one of the account's own.</summary>
    public bool IsSubscription { get; private set; }

    public bool IsSelected { get; private set; }

    /// <summary>
    /// Where the user's SANKORÉ rendez-vous are copied (lot 4). At most one per user — held by a
    /// filtered unique index — and never a read-only agenda.
    /// </summary>
    public bool IsAppointmentTarget { get; private set; }

    /// <summary>The provider's delta link (Microsoft) or sync token (Google). Opaque, never shown.</summary>
    public string? SyncCursor { get; private set; }

    /// <summary>The start of the window the cursor was opened with.</summary>
    public DateTimeOffset? WindowStart { get; private set; }

    public DateTimeOffset? LastSyncAt { get; private set; }
    public string? LastError { get; private set; }
    public int EventCount { get; private set; }

    public static ConnectedCalendar Discover(
        Guid tenantId, Guid accountId, Guid userId, string externalCalendarId, string name, string? color,
        bool isPrimary, bool isReadOnly, bool isSubscription) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        AccountId = accountId,
        UserId = userId,
        ExternalCalendarId = externalCalendarId,
        Name = Clip(name, 200) ?? "Agenda",
        Color = Clip(color, 20),
        IsPrimary = isPrimary,
        IsReadOnly = isReadOnly,
        IsSubscription = isSubscription,
        // Everything the account holds is shown at first — the screen's own default — and the
        // user unticks what they do not want; a subscription included, it is usually wanted.
        IsSelected = true,
    };

    public void Refresh(string name, string? color, bool isPrimary, bool isReadOnly, bool isSubscription)
    {
        Name = Clip(name, 200) ?? Name;
        Color = Clip(color, 20);
        IsPrimary = isPrimary;
        IsReadOnly = isReadOnly;
        IsSubscription = isSubscription;
    }

    /// <summary>Unticking forgets the cursor: ticking it again reads it whole.</summary>
    public void Select(bool selected)
    {
        IsSelected = selected;
        if (!selected)
        {
            SyncCursor = null;
            WindowStart = null;
            EventCount = 0;
        }
    }

    public Result MakeAppointmentTarget()
    {
        if (IsReadOnly) return Result.Fail("CALENDAR_READ_ONLY");
        IsAppointmentTarget = true;
        return Result.Ok();
    }

    public void ClearAppointmentTarget() => IsAppointmentTarget = false;

    /// <summary>Whether the next run must rebuild from scratch: no cursor, or a window grown stale.</summary>
    public bool NeedsFullSync(DateTimeOffset now) =>
        SyncCursor is null || WindowStart is not { } start || now - (start + WindowBack) > WindowMaxAge;

    public void RecordSync(string? cursor, DateTimeOffset windowStart, int eventCount, DateTimeOffset now)
    {
        SyncCursor = cursor;
        WindowStart = windowStart;
        EventCount = eventCount;
        LastSyncAt = now;
        LastError = null;
    }

    public void RecordFailure(string code)
    {
        LastError = Clip(code, 60);
    }

    /// <summary>The provider forgot the cursor (Google 410): the next run reads the agenda whole.</summary>
    public void ResetCursor()
    {
        SyncCursor = null;
        WindowStart = null;
    }

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

/// <summary>
/// One event read from a connected agenda (lot 2). <b>Title and location are encrypted</b> and
/// readable by the agenda's owner alone (owner's decision): a colleague's view — lot 6 — sees the
/// slot and <see cref="ShowAs"/>, never what it is. A recurring event is stored as its occurrences,
/// the providers expanding them inside the window.
/// </summary>
public sealed class ConnectedCalendarEvent : AggregateRoot
{
    private ConnectedCalendarEvent() { }

    public Guid Id { get; private set; }
    public Guid CalendarId { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid UserId { get; private set; }
    public string ExternalEventId { get; private set; } = default!;
    public DateTimeOffset StartAt { get; private set; }
    public DateTimeOffset EndAt { get; private set; }
    public bool IsAllDay { get; private set; }
    public CalendarShowAs ShowAs { get; private set; }

    /// <summary>Ciphertext (Integration field key). Null when the event has no title.</summary>
    public string? TitleEnc { get; private set; }

    /// <summary>Ciphertext (Integration field key).</summary>
    public string? LocationEnc { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ConnectedCalendarEvent Create(
        Guid tenantId, ConnectedCalendar calendar, string externalEventId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        CalendarId = calendar.Id,
        AccountId = calendar.AccountId,
        UserId = calendar.UserId,
        ExternalEventId = externalEventId,
        UpdatedAt = now,
    };

    public void Apply(
        DateTimeOffset startAt, DateTimeOffset endAt, bool isAllDay, CalendarShowAs showAs,
        string? titleEnc, string? locationEnc, DateTimeOffset now)
    {
        StartAt = startAt;
        EndAt = endAt < startAt ? startAt : endAt;
        IsAllDay = isAllDay;
        ShowAs = showAs;
        TitleEnc = titleEnc;
        LocationEnc = locationEnc;
        UpdatedAt = now;
    }
}

public enum AppointmentCopyStatus
{
    /// <summary>The provider holds the copy as it should be.</summary>
    Synced,

    /// <summary>The last attempt failed; the 15-minute job tries again.</summary>
    Failed,
}

/// <summary>
/// One SANKORÉ rendez-vous copied into one participant's target agenda (lot 4). The appointment is
/// M13's and stays the reference: this row only remembers WHERE its copy lives, so a move updates
/// it and a cancellation deletes it instead of leaving an orphan in somebody's Outlook.
///
/// <para>
/// <see cref="ContentHash"/> is a hash of what was sent: a change that does not alter the copy
/// (an invitation added to somebody else) costs no provider call.
/// </para>
/// </summary>
public sealed class AppointmentCalendarCopy : AggregateRoot
{
    /// <summary>Past this many failed attempts in a row the job stops retrying; the next change does.</summary>
    public const int MaxAttempts = 10;

    private AppointmentCalendarCopy() { }

    public Guid Id { get; private set; }
    public Guid AppointmentId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid CalendarId { get; private set; }

    /// <summary>The provider's event id, once created.</summary>
    public string? ExternalEventId { get; private set; }

    public string? ContentHash { get; private set; }
    public AppointmentCopyStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static AppointmentCalendarCopy Start(Guid tenantId, Guid appointmentId, ConnectedCalendar target, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        AppointmentId = appointmentId,
        UserId = target.UserId,
        AccountId = target.AccountId,
        CalendarId = target.Id,
        Status = AppointmentCopyStatus.Failed,
        UpdatedAt = now,
    };

    public void RecordSynced(string externalEventId, string contentHash, DateTimeOffset now)
    {
        ExternalEventId = externalEventId;
        ContentHash = contentHash;
        Status = AppointmentCopyStatus.Synced;
        Attempts = 0;
        LastError = null;
        UpdatedAt = now;
    }

    public void RecordFailure(string code, DateTimeOffset now)
    {
        Status = AppointmentCopyStatus.Failed;
        Attempts++;
        LastError = code.Length <= 60 ? code : code[..60];
        UpdatedAt = now;
    }

    /// <summary>The provider no longer has it (deleted by hand): the next attempt creates it again.</summary>
    public void ForgetExternalEvent() => ExternalEventId = null;
}

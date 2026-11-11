namespace Sankore.Admin.Domain;

/// <summary>
/// What an institution is to the platform commercially (free trial, <c>docs/essai-gratuit-socle.md</c>).
/// Every institution created before the trial existed is <see cref="Paid"/>, the zero value, so a row
/// written by an older binary reads as what it always was.
/// </summary>
public enum TenantOffer
{
    Paid = 0,
    Trial = 1,
}

public enum TenantSignupStatus
{
    /// <summary>Received; the visitor has not opened the confirmation link yet.</summary>
    PendingEmail,

    /// <summary>The address is confirmed; waiting for the platform's decision (product owner, D3).</summary>
    Confirmed,

    /// <summary>The institution exists (<see cref="TenantSignup.TenantId"/>).</summary>
    Approved,

    Rejected,
}

/// <summary>
/// A free-trial request from the Talisoft website (lot 1). The registry keeps the request and takes
/// the decision; it sends nothing — Sankore.Api mails the confirmation link and, later, the decision.
///
/// <para>
/// <b>Only the SHA-256 of the confirmation token is stored.</b> The clear token is minted by
/// Sankore.Api and lives in one e-mail, the way the remote-KYC link does: a registry dump must not
/// hand anyone a way to confirm a request in somebody else's name.
/// </para>
///
/// <para>
/// <b>An unconfirmed request expires.</b> It is not swept: <see cref="IsExpiredAt"/> compares the
/// clock, the listing shows it as expired, and its name and address are free again for a new request.
/// </para>
/// </summary>
public sealed class TenantSignup
{
    public const int InstitutionNameMaxLength = 120;
    public const int PersonNameMaxLength = 80;
    public const int EmailMaxLength = 100;   // Tenant.RootUserEmail's column
    public const int PhoneMaxLength = 32;
    public const int CountryMaxLength = 2;   // ISO 3166-1 alpha-2
    public const int ActivityMaxLength = 40;
    public const int IpMaxLength = 64;
    public const int UserAgentMaxLength = 512;
    public const int ReasonMaxLength = 1000;
    public const int TokenHashLength = 64;   // hex SHA-256

    public Guid Id { get; private set; }
    public string InstitutionName { get; private set; } = null!;
    public string Slug { get; private set; } = null!;
    public string FirstName { get; private set; } = null!;
    public string LastName { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public string Phone { get; private set; } = null!;
    public string Country { get; private set; } = null!;
    public string ActivityType { get; private set; } = null!;
    public string? RequestIp { get; private set; }
    public string? UserAgent { get; private set; }

    /// <summary>The language of the website page the request came from: the requester's e-mails follow it.</summary>
    public string Locale { get; private set; } = Sankore.Shared.Kernel.LanguageCode.Default;

    public string TokenHash { get; private set; } = null!;
    public DateTimeOffset TokenExpiresAt { get; private set; }

    public TenantSignupStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public Guid? DecidedByAdminId { get; private set; }
    public string? RejectionReason { get; private set; }

    /// <summary>The institution created by the approval.</summary>
    public Guid? TenantId { get; private set; }

    /// <summary>
    /// When Sankore.Api mailed the decision to the requester (lot 2). Null on a decided request means
    /// « still to tell » — the API's job reads that feed, the registry having no mail of its own.
    /// </summary>
    public DateTimeOffset? DecisionNotifiedAt { get; private set; }

    private TenantSignup() { }

    public static TenantSignup Create(
        string institutionName, string slug, string firstName, string lastName, string email, string phone,
        string country, string activityType, string? requestIp, string? userAgent,
        string tokenHash, DateTimeOffset tokenExpiresAt, DateTimeOffset now, string? locale = null) => new()
    {
        Locale = Sankore.Shared.Kernel.LanguageCode.Normalize(locale),
        Id = Guid.NewGuid(),
        InstitutionName = institutionName,
        Slug = slug,
        FirstName = firstName,
        LastName = lastName,
        Email = email,
        Phone = phone,
        Country = country,
        ActivityType = activityType,
        RequestIp = requestIp,
        UserAgent = userAgent,
        TokenHash = tokenHash,
        TokenExpiresAt = tokenExpiresAt,
        Status = TenantSignupStatus.PendingEmail,
        CreatedAt = now,
    };

    /// <summary>Unconfirmed and past its link's window: dead, and holding nothing any more.</summary>
    public bool IsExpiredAt(DateTimeOffset now) => Status == TenantSignupStatus.PendingEmail && TokenExpiresAt <= now;

    /// <summary>
    /// Still holds its name and address against a new request: waiting for its link, or for the
    /// platform. An approved request no longer needs to — the institution it created holds them.
    /// </summary>
    public bool IsOpenAt(DateTimeOffset now) =>
        Status == TenantSignupStatus.Confirmed || (Status == TenantSignupStatus.PendingEmail && !IsExpiredAt(now));

    /// <summary>
    /// True when the link moved the request forward, or had already done so (a second click). False
    /// for an expired link, or a request already decided.
    /// </summary>
    public bool Confirm(DateTimeOffset now)
    {
        if (Status == TenantSignupStatus.Confirmed) return true;
        if (Status != TenantSignupStatus.PendingEmail || IsExpiredAt(now)) return false;

        Status = TenantSignupStatus.Confirmed;
        ConfirmedAt = now;
        return true;
    }

    /// <summary>Only a confirmed request: an unconfirmed address may be somebody else's.</summary>
    public void Approve(Guid tenantId, Guid? adminId, DateTimeOffset now)
    {
        if (Status != TenantSignupStatus.Confirmed)
            throw new InvalidOperationException($"A {Status} request cannot be approved.");

        Status = TenantSignupStatus.Approved;
        TenantId = tenantId;
        DecidedAt = now;
        DecidedByAdminId = adminId;
    }

    /// <summary>
    /// A request waiting for its link or for the platform. An expired one may be refused too, so it
    /// leaves the queue with a reason.
    /// </summary>
    public void Reject(string reason, Guid? adminId, DateTimeOffset now)
    {
        if (Status is not (TenantSignupStatus.PendingEmail or TenantSignupStatus.Confirmed))
            throw new InvalidOperationException($"A {Status} request cannot be rejected.");

        Status = TenantSignupStatus.Rejected;
        RejectionReason = reason;
        DecidedAt = now;
        DecidedByAdminId = adminId;
    }

    public void MarkDecisionNotified(DateTimeOffset now) => DecisionNotifiedAt ??= now;
}

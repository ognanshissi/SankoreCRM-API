using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;

namespace Sankore.Admin.Features.TenantSignups;

/// <summary>
/// The free trial's settings, bound from <c>TrialSignup</c> (<c>docs/essai-gratuit-socle.md</c>).
/// </summary>
public sealed class TenantSignupOptions
{
    public const string SectionName = "TrialSignup";

    /// <summary>
    /// The address of a trial institution, <c>{slug}</c> standing for the name it chose:
    /// <c>https://{slug}.talisoftcrm.com</c> in production, <c>http://{slug}.localhost:4222</c> in
    /// development. Empty = the trial is not open on this platform, and every route answers 503
    /// <see cref="TenantSignupErrors.NotConfigured"/>. In production it must agree with Sankore.Api's
    /// <c>Cors:AllowedOriginSuffixes</c> and with the Traefik router.
    /// </summary>
    public string AddressTemplate { get; set; } = "";

    /// <summary>Length of the trial, counted from the approval (product owner, D4).</summary>
    public int TrialDays { get; set; } = 30;

    /// <summary>How long the confirmation link stays usable.</summary>
    public TimeSpan ConfirmationWindow { get; set; } = TimeSpan.FromHours(48);

    public const string SlugToken = "{slug}";

    /// <summary>
    /// The template, normalised, or null when absent or malformed: <c>{slug}</c> exactly once, as
    /// the FIRST label of the host, and an origin once replaced (scheme, host, optional port,
    /// nothing else — what <c>TenantFqdn.IsOrigin</c> accepts and the front sends).
    /// </summary>
    public string? ValidTemplate()
    {
        var template = AddressTemplate.Trim().ToLowerInvariant().TrimEnd('/');
        var at = template.IndexOf(SlugToken, StringComparison.Ordinal);
        if (at < 0 || template.IndexOf(SlugToken, at + 1, StringComparison.Ordinal) >= 0) return null;
        if (!template.StartsWith("https://" + SlugToken + ".", StringComparison.Ordinal)
            && !template.StartsWith("http://" + SlugToken + ".", StringComparison.Ordinal)) return null;
        return TenantFqdn.IsOrigin(template.Replace(SlugToken, "sample"), out _) ? template : null;
    }

    /// <summary>The origin of <paramref name="slug"/> under a template <see cref="ValidTemplate"/> accepted.</summary>
    public static string OriginFor(string template, string slug) => TenantFqdn.Normalize(template.Replace(SlugToken, slug));
}

public static class TenantSignupErrors
{
    public const string NotConfigured = "TRIAL_SIGNUP_NOT_CONFIGURED";
    public const string Invalid = "TRIAL_SIGNUP_INVALID";
    public const string NotFound = "TRIAL_SIGNUP_NOT_FOUND";
    public const string EmailInUse = "TRIAL_EMAIL_IN_USE";
    public const string TokenUnknown = "TRIAL_TOKEN_UNKNOWN";
    public const string TokenExpired = "TRIAL_TOKEN_EXPIRED";
    public const string NotConfirmed = "TRIAL_SIGNUP_NOT_CONFIRMED";
    public const string AlreadyDecided = "TRIAL_SIGNUP_ALREADY_DECIDED";
    public const string ReasonRequired = "TRIAL_REJECTION_REASON_REQUIRED";
}

/// <summary>What a request looks like on screen: the stored status, with « expired » read off the clock.</summary>
public enum TenantSignupView
{
    PendingEmail,
    Expired,
    Confirmed,
    Approved,
    Rejected,
}

public sealed record TenantSignupResult<T>(T? Value, string? Error = null, string? Detail = null,
    IReadOnlyDictionary<string, string>? Errors = null)
{
    public bool IsSuccess => Error is null;
    public static TenantSignupResult<T> Ok(T value) => new(value);
    public static TenantSignupResult<T> Fail(string error, string? detail = null) => new(default, error, detail);
    public static TenantSignupResult<T> Invalid(IReadOnlyDictionary<string, string> errors) =>
        new(default, TenantSignupErrors.Invalid, "Certains champs sont incorrects.", errors);
}

internal static class TenantSignupHttp
{
    public static IResult ToHttp<T>(this TenantSignupResult<T> result, Func<T, IResult> onSuccess)
    {
        if (result.IsSuccess) return onSuccess(result.Value!);

        var body = new { error = result.Error, detail = result.Detail, errors = result.Errors };
        return result.Error switch
        {
            TenantSignupErrors.NotConfigured => Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable),
            TenantSignupErrors.NotFound or TenantSignupErrors.TokenUnknown => Results.NotFound(body),
            TenantSignupErrors.TokenExpired => Results.Json(body, statusCode: StatusCodes.Status410Gone),
            TenantSignupErrors.Invalid or TenantSignupErrors.ReasonRequired or TrialSlug.Invalid or TrialSlug.Reserved
                => Results.BadRequest(body),
            _ => Results.Conflict(body),
        };
    }

    public static (int Page, int Size) Paging(int? page, int? pageSize) =>
        (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 25, 1, 100));
}

/// <summary>The few rules shared by the creation, the availability check and the approval.</summary>
internal static class TenantSignupRules
{
    public static TenantSignupView ViewOf(TenantSignup s, DateTimeOffset now) => s.Status switch
    {
        TenantSignupStatus.PendingEmail => s.IsExpiredAt(now) ? TenantSignupView.Expired : TenantSignupView.PendingEmail,
        TenantSignupStatus.Confirmed => TenantSignupView.Confirmed,
        TenantSignupStatus.Approved => TenantSignupView.Approved,
        _ => TenantSignupView.Rejected,
    };

    /// <summary>
    /// <see langword="null"/> when <paramref name="slug"/> can be given, else a <see cref="TrialSlug"/>
    /// code. Taken = an institution's main or secondary address, or an OPEN request other than
    /// <paramref name="exceptSignupId"/>: an expired, refused or approved request holds nothing (the
    /// approved one's institution does).
    /// </summary>
    public static async Task<string?> SlugProblemAsync(AdminDbContext db, string? slug, string suffix,
        Guid? exceptSignupId, DateTimeOffset now, CancellationToken ct)
    {
        if (TrialSlug.Check(slug) is { } problem) return problem;

        var fqdn = TenantSignupOptions.OriginFor(suffix, slug!);
        var taken = await db.Tenants.AnyAsync(t => t.Fqdn == fqdn, ct)
                    || await db.TenantDomains.AnyAsync(d => d.Fqdn == fqdn, ct)
                    || await db.TenantSignups.AnyAsync(s => s.Slug == slug && s.Id != exceptSignupId
                        && (s.Status == TenantSignupStatus.Confirmed
                            || (s.Status == TenantSignupStatus.PendingEmail && s.TokenExpiresAt > now)), ct);
        return taken ? TrialSlug.Taken : null;
    }

    /// <summary>
    /// An address already the root of an institution (a unique column of <c>Tenants</c>), or carried
    /// by another open request. Said to Sankore.Api, which must NOT repeat it to the public.
    /// </summary>
    public static async Task<bool> EmailInUseAsync(AdminDbContext db, string email, Guid? exceptSignupId,
        DateTimeOffset now, CancellationToken ct) =>
        await db.Tenants.AnyAsync(t => t.RootUserEmail.ToLower() == email, ct)
        || await db.TenantSignups.AnyAsync(s => s.Email == email && s.Id != exceptSignupId
            && (s.Status == TenantSignupStatus.Confirmed
                || (s.Status == TenantSignupStatus.PendingEmail && s.TokenExpiresAt > now)), ct);

    public static string? NormalizeEmail(string? email)
    {
        var trimmed = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > TenantSignup.EmailMaxLength) return null;
        if (!MailAddress.TryCreate(trimmed, out var parsed) || parsed.Address != trimmed) return null;
        // A host with a dot: « jean@localhost » is a valid address and never a professional one.
        return parsed.Host.Contains('.') ? trimmed : null;
    }

    public static bool IsTokenHash(string? value) =>
        value is { Length: TenantSignup.TokenHashLength } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string? Clip(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

/// <summary>A request as the platform's queue and detail show it.</summary>
public sealed record TenantSignupSummary(
    Guid Id,
    string InstitutionName,
    string Slug,
    string? Fqdn,
    string FirstName,
    string LastName,
    string Email,
    string Phone,
    string Country,
    string ActivityType,
    TenantSignupView Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? DecidedAt,
    string? RejectionReason,
    Guid? TenantId)
{
    internal static TenantSignupSummary From(TenantSignup s, string? suffix, DateTimeOffset now) => new(
        s.Id, s.InstitutionName, s.Slug, suffix is null ? null : TenantSignupOptions.OriginFor(suffix, s.Slug),
        s.FirstName, s.LastName, s.Email, s.Phone, s.Country, s.ActivityType,
        TenantSignupRules.ViewOf(s, now), s.CreatedAt, s.ConfirmedAt, s.DecidedAt, s.RejectionReason, s.TenantId);
}

public sealed record TenantSignupPage(IReadOnlyList<TenantSignupSummary> Items, int Total, int Page, int PageSize);

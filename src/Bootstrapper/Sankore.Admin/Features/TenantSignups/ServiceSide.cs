using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Admin.Features.TenantSignups;

// What Sankore.Api calls with the service key: its public sign-up route (lot 2) is the only door a
// visitor has. The registry itself is never exposed to the public — whoever reaches it with the key
// administers every institution. The API verifies the captcha, limits the rate and mints the
// confirmation token; the registry checks the request and stores it.

// ── Availability ────────────────────────────────────────────────────────────────────────────────

public sealed record TrialSlugAvailability(string? Slug, string? Fqdn, string Status);

public static class TrialSlugAvailabilityEndpoint
{
    public const string Available = "Available";

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/slug-availability", async ([FromQuery] string? slug, ISender sender, CancellationToken ct) =>
                (await sender.Send(new CheckTrialSlugQuery(slug), ct)).ToHttp(Results.Ok))
            .WithName("CheckTrialSlug")
            .WithSummary("Whether a free-trial address can be given")
            .WithDescription("Status: Available, TRIAL_SLUG_INVALID, TRIAL_SLUG_RESERVED or TRIAL_SLUG_TAKEN. "
                + "Says nothing about WHO holds a taken name.")
            .Produces<TrialSlugAvailability>()
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi();
    }
}

public sealed record CheckTrialSlugQuery(string? Slug) : IRequest<TenantSignupResult<TrialSlugAvailability>>;

internal sealed class CheckTrialSlugHandler(AdminDbContext db, IOptions<TenantSignupOptions> options, TimeProvider clock)
    : IRequestHandler<CheckTrialSlugQuery, TenantSignupResult<TrialSlugAvailability>>
{
    public async Task<TenantSignupResult<TrialSlugAvailability>> Handle(CheckTrialSlugQuery query, CancellationToken ct)
    {
        if (options.Value.ValidTemplate() is not { } suffix)
            return TenantSignupResult<TrialSlugAvailability>.Fail(TenantSignupErrors.NotConfigured);

        var slug = TrialSlug.Normalize(query.Slug);
        var problem = await TenantSignupRules.SlugProblemAsync(db, slug, suffix, null, clock.GetUtcNow(), ct);
        return TenantSignupResult<TrialSlugAvailability>.Ok(new TrialSlugAvailability(
            slug, problem == TrialSlug.Invalid ? null : TenantSignupOptions.OriginFor(suffix, slug!),
            problem ?? TrialSlugAvailabilityEndpoint.Available));
    }
}

// ── Create ──────────────────────────────────────────────────────────────────────────────────────

public static class CreateTenantSignupEndpoint
{
    /// <param name="TokenHash">Lower-case hex SHA-256 of the confirmation token Sankore.Api mailed.</param>
    /// <param name="Country">ISO 3166-1 alpha-2 (<c>CI</c>, <c>SN</c>…).</param>
    public sealed record Request(
        string InstitutionName,
        string Slug,
        string FirstName,
        string LastName,
        string Email,
        string Phone,
        string Country,
        string ActivityType,
        string TokenHash,
        string? RequestIp,
        string? UserAgent,
        string? Locale = null);

    public sealed record Response(Guid Id, string Fqdn, DateTimeOffset TokenExpiresAt);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/", async (Request body, ISender sender, CancellationToken ct) =>
                (await sender.Send(new CreateTenantSignupCommand(body), ct))
                .ToHttp(r => Results.Created($"/api/v1/tenant-signups/{r.Id}", r)))
            .WithName("CreateTenantSignup")
            .WithSummary("Record a free-trial request from the website, waiting for its e-mail confirmation")
            .WithDescription("400 TRIAL_SIGNUP_INVALID (errors by field) / TRIAL_SLUG_INVALID / TRIAL_SLUG_RESERVED; "
                + "409 TRIAL_SLUG_TAKEN / TRIAL_EMAIL_IN_USE. TRIAL_EMAIL_IN_USE is for the API only: "
                + "the public must get the same answer as for a new address.")
            .Produces<Response>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi();
    }
}

public sealed record CreateTenantSignupCommand(CreateTenantSignupEndpoint.Request Body)
    : IRequest<TenantSignupResult<CreateTenantSignupEndpoint.Response>>;

internal sealed class CreateTenantSignupHandler(AdminDbContext db, IOptions<TenantSignupOptions> options, TimeProvider clock)
    : IRequestHandler<CreateTenantSignupCommand, TenantSignupResult<CreateTenantSignupEndpoint.Response>>
{
    public async Task<TenantSignupResult<CreateTenantSignupEndpoint.Response>> Handle(CreateTenantSignupCommand command,
        CancellationToken ct)
    {
        if (options.Value.ValidTemplate() is not { } suffix)
            return TenantSignupResult<CreateTenantSignupEndpoint.Response>.Fail(TenantSignupErrors.NotConfigured);

        var b = command.Body;
        var errors = new Dictionary<string, string>();

        var institution = Required(b.InstitutionName, TenantSignup.InstitutionNameMaxLength, 2, "institutionName",
            "Indiquez le nom de l'institution.", errors);
        var firstName = Required(b.FirstName, TenantSignup.PersonNameMaxLength, 1, "firstName", "Indiquez votre prénom.", errors);
        var lastName = Required(b.LastName, TenantSignup.PersonNameMaxLength, 1, "lastName", "Indiquez votre nom.", errors);
        var activity = Required(b.ActivityType, TenantSignup.ActivityMaxLength, 1, "activityType",
            "Indiquez votre type d'activité.", errors);

        var email = TenantSignupRules.NormalizeEmail(b.Email);
        if (email is null) errors["email"] = "Indiquez une adresse e-mail valide.";

        var phone = b.Phone?.Trim();
        if (phone is null || phone.Length is < 6 or > TenantSignup.PhoneMaxLength
            || !phone.All(c => char.IsAsciiDigit(c) || c is '+' or ' ' or '-' or '.' or '(' or ')')
            || phone.Count(char.IsAsciiDigit) < 6)
            errors["phone"] = "Indiquez un numéro de téléphone valide.";

        var country = b.Country?.Trim().ToUpperInvariant();
        if (country is not { Length: 2 } || !country.All(char.IsAsciiLetterUpper))
            errors["country"] = "Choisissez un pays.";

        if (!TenantSignupRules.IsTokenHash(b.TokenHash))
            errors["tokenHash"] = "Empreinte du jeton de confirmation invalide.";

        var slug = TrialSlug.Normalize(b.Slug);
        var slugProblem = TrialSlug.Check(slug);
        if (slugProblem == TrialSlug.Invalid)
            errors["slug"] = $"L'adresse doit compter {TrialSlug.MinLength} à {TrialSlug.MaxLength} caractères : "
                             + "lettres minuscules sans accent, chiffres et tirets.";

        if (errors.Count > 0) return TenantSignupResult<CreateTenantSignupEndpoint.Response>.Invalid(errors);
        if (slugProblem == TrialSlug.Reserved)
            return TenantSignupResult<CreateTenantSignupEndpoint.Response>.Fail(TrialSlug.Reserved,
                "Cette adresse est réservée.");

        var now = clock.GetUtcNow();
        if (await TenantSignupRules.SlugProblemAsync(db, slug, suffix, null, now, ct) is { } taken)
            return TenantSignupResult<CreateTenantSignupEndpoint.Response>.Fail(taken, "Cette adresse est déjà prise.");
        if (await TenantSignupRules.EmailInUseAsync(db, email!, null, now, ct))
            return TenantSignupResult<CreateTenantSignupEndpoint.Response>.Fail(TenantSignupErrors.EmailInUse);

        var signup = TenantSignup.Create(institution!, slug!, firstName!, lastName!, email!, phone!, country!, activity!,
            TenantSignupRules.Clip(b.RequestIp, TenantSignup.IpMaxLength),
            TenantSignupRules.Clip(b.UserAgent, TenantSignup.UserAgentMaxLength),
            b.TokenHash, now + options.Value.ConfirmationWindow, now, b.Locale);
        db.TenantSignups.Add(signup);
        await db.SaveChangesAsync(ct);

        return TenantSignupResult<CreateTenantSignupEndpoint.Response>.Ok(
            new CreateTenantSignupEndpoint.Response(signup.Id, TenantSignupOptions.OriginFor(suffix, slug!), signup.TokenExpiresAt));
    }

    private static string? Required(string? value, int max, int min, string field, string message,
        Dictionary<string, string> errors)
    {
        var trimmed = value?.Trim();
        if (trimmed is null || trimmed.Length < min) errors[field] = message;
        else if (trimmed.Length > max) errors[field] = $"{max} caractères au plus.";
        else return trimmed;
        return null;
    }
}

// ── Confirm ─────────────────────────────────────────────────────────────────────────────────────

public static class ConfirmTenantSignupEndpoint
{
    public sealed record Request(string TokenHash);

    /// <param name="NewlyConfirmed">False for a second click: the API tells the platform team once.</param>
    public sealed record Response(Guid Id, string InstitutionName, string Email, bool NewlyConfirmed,
        string FirstName, string LastName, string Phone, string Country, string ActivityType, string Slug, string Locale);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/confirm", async (Request body, ISender sender, CancellationToken ct) =>
                (await sender.Send(new ConfirmTenantSignupCommand(body.TokenHash), ct)).ToHttp(Results.Ok))
            .WithName("ConfirmTenantSignup")
            .WithSummary("Confirm a free-trial request's e-mail address by the hash of its link's token")
            .WithDescription("404 TRIAL_TOKEN_UNKNOWN; 410 TRIAL_TOKEN_EXPIRED; 409 TRIAL_SIGNUP_ALREADY_DECIDED. "
                + "A second confirmation answers 200 with newlyConfirmed=false.")
            .Produces<Response>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status410Gone)
            .WithOpenApi();
    }
}

public sealed record ConfirmTenantSignupCommand(string? TokenHash) : IRequest<TenantSignupResult<ConfirmTenantSignupEndpoint.Response>>;

internal sealed class ConfirmTenantSignupHandler(AdminDbContext db, TimeProvider clock)
    : IRequestHandler<ConfirmTenantSignupCommand, TenantSignupResult<ConfirmTenantSignupEndpoint.Response>>
{
    public async Task<TenantSignupResult<ConfirmTenantSignupEndpoint.Response>> Handle(ConfirmTenantSignupCommand command,
        CancellationToken ct)
    {
        var hash = command.TokenHash?.Trim().ToLowerInvariant();
        if (!TenantSignupRules.IsTokenHash(hash))
            return TenantSignupResult<ConfirmTenantSignupEndpoint.Response>.Fail(TenantSignupErrors.TokenUnknown);

        var signup = await db.TenantSignups.AsTracking().FirstOrDefaultAsync(s => s.TokenHash == hash, ct);
        if (signup is null)
            return TenantSignupResult<ConfirmTenantSignupEndpoint.Response>.Fail(TenantSignupErrors.TokenUnknown);

        var now = clock.GetUtcNow();
        var wasConfirmed = signup.Status == TenantSignupStatus.Confirmed;
        if (!signup.Confirm(now))
            return TenantSignupResult<ConfirmTenantSignupEndpoint.Response>.Fail(signup.IsExpiredAt(now)
                ? TenantSignupErrors.TokenExpired
                : TenantSignupErrors.AlreadyDecided);

        if (!wasConfirmed) await db.SaveChangesAsync(ct);
        return TenantSignupResult<ConfirmTenantSignupEndpoint.Response>.Ok(
            new ConfirmTenantSignupEndpoint.Response(signup.Id, signup.InstitutionName, signup.Email, !wasConfirmed,
                signup.FirstName, signup.LastName, signup.Phone, signup.Country, signup.ActivityType, signup.Slug, signup.Locale));
    }
}

// ── Decisions still to tell the requester (lot 2's job) ─────────────────────────────────────────

public sealed record TenantSignupDecision(
    Guid Id,
    TenantSignupStatus Status,
    string InstitutionName,
    string FirstName,
    string Email,
    string? Fqdn,
    string? RejectionReason,
    Guid? TenantId,
    DateTimeOffset DecidedAt,
    string Locale);

public static class TenantSignupDecisionFeedEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/decisions-to-notify", async ([FromQuery] int? take, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new ListTenantSignupDecisionsQuery(Math.Clamp(take ?? 50, 1, 200)), ct)))
            .WithName("ListTenantSignupDecisionsToNotify")
            .WithSummary("Approved or rejected requests whose requester has not been told yet, oldest decision first")
            .Produces<IReadOnlyList<TenantSignupDecision>>()
            .WithOpenApi();

        group.MapPost("/{signupId:guid}/decision-notified", async (Guid signupId, ISender sender, CancellationToken ct) =>
                (await sender.Send(new MarkTenantSignupDecisionNotifiedCommand(signupId), ct)).ToHttp(_ => Results.NoContent()))
            .WithName("MarkTenantSignupDecisionNotified")
            .WithSummary("Record that the requester was told the decision (idempotent)")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();
    }
}

public sealed record ListTenantSignupDecisionsQuery(int Take) : IRequest<IReadOnlyList<TenantSignupDecision>>;

internal sealed class ListTenantSignupDecisionsHandler(AdminDbContext db, IOptions<TenantSignupOptions> options)
    : IRequestHandler<ListTenantSignupDecisionsQuery, IReadOnlyList<TenantSignupDecision>>
{
    public async Task<IReadOnlyList<TenantSignupDecision>> Handle(ListTenantSignupDecisionsQuery query, CancellationToken ct)
    {
        var suffix = options.Value.ValidTemplate();
        var rows = await db.TenantSignups.AsNoTracking()
            .Where(s => (s.Status == TenantSignupStatus.Approved || s.Status == TenantSignupStatus.Rejected)
                        && s.DecisionNotifiedAt == null && s.DecidedAt != null)
            .OrderBy(s => s.DecidedAt)
            .Take(query.Take)
            .ToListAsync(ct);

        // The institution's own address when it exists: the platform may have changed it since.
        var tenantIds = rows.Where(s => s.TenantId != null).Select(s => s.TenantId!.Value).ToList();
        var fqdns = await db.Tenants.AsNoTracking().Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Fqdn, ct);

        return rows.Select(s => new TenantSignupDecision(
                s.Id, s.Status, s.InstitutionName, s.FirstName, s.Email,
                s.TenantId is { } id && fqdns.TryGetValue(id, out var fqdn) ? fqdn
                    : suffix is null ? null : TenantSignupOptions.OriginFor(suffix, s.Slug),
                s.RejectionReason, s.TenantId, s.DecidedAt!.Value, s.Locale))
            .ToList();
    }
}

public sealed record MarkTenantSignupDecisionNotifiedCommand(Guid SignupId) : IRequest<TenantSignupResult<bool>>;

internal sealed class MarkTenantSignupDecisionNotifiedHandler(AdminDbContext db, TimeProvider clock)
    : IRequestHandler<MarkTenantSignupDecisionNotifiedCommand, TenantSignupResult<bool>>
{
    public async Task<TenantSignupResult<bool>> Handle(MarkTenantSignupDecisionNotifiedCommand command, CancellationToken ct)
    {
        var signup = await db.TenantSignups.AsTracking().FirstOrDefaultAsync(s => s.Id == command.SignupId, ct);
        if (signup is null) return TenantSignupResult<bool>.Fail(TenantSignupErrors.NotFound);
        if (signup.DecidedAt is null) return TenantSignupResult<bool>.Fail(TenantSignupErrors.NotConfirmed);

        signup.MarkDecisionNotified(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return TenantSignupResult<bool>.Ok(true);
    }
}

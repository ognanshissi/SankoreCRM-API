using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sankore.Admin.Domain;
using Sankore.Admin.Features.Tenants.CreateTenant;
using Sankore.Admin.Infrastructure;
using Sankore.Admin.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Admin.Features.TenantSignups;

// The platform's queue of free-trial requests (product owner, D3: the platform approves each one at
// launch). Every route carries RequirePlatformAdmin: with the service key alone, Sankore.Api could
// approve the requests its own public route lets in.

/// <summary>What the queue's tabs count, one per <see cref="TenantSignupView"/>.</summary>
public sealed record TenantSignupCounts(int PendingEmailCount, int ExpiredCount, int ConfirmedCount, int ApprovedCount,
    int RejectedCount);

/// <param name="SameIp">Other requests sent from the same address, newest first (10 at most).</param>
/// <param name="SameEmailDomain">Other requests whose e-mail has the same domain (10 at most).</param>
public sealed record TenantSignupDetail(
    TenantSignupSummary Signup,
    string? RequestIp,
    string? UserAgent,
    string? DecidedByAdminName,
    IReadOnlyList<TenantSignupSummary> SameIp,
    IReadOnlyList<TenantSignupSummary> SameEmailDomain);

// ── Queue ───────────────────────────────────────────────────────────────────────────────────────

public static class ListTenantSignupsEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/", async ([FromQuery] TenantSignupView? status, [FromQuery] string? search, [FromQuery] int? page,
                    [FromQuery] int? pageSize, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new ListTenantSignupsQuery(status, search, page, pageSize), ct)))
            .RequirePlatformAdmin()
            .WithName("ListTenantSignups")
            .WithSummary("Free-trial requests. Confirmed ones (waiting for a decision) are served oldest first, the rest newest first")
            .Produces<TenantSignupPage>()
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        group.MapGet("/counts", async (ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new CountTenantSignupsQuery(), ct)))
            .RequirePlatformAdmin()
            .WithName("CountTenantSignups")
            .WithSummary("How many free-trial requests per status")
            .Produces<TenantSignupCounts>()
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();
    }
}

public sealed record ListTenantSignupsQuery(TenantSignupView? Status, string? Search, int? Page, int? PageSize)
    : IRequest<TenantSignupPage>;

internal sealed class ListTenantSignupsHandler(AdminDbContext db, IOptions<TenantSignupOptions> options, TimeProvider clock)
    : IRequestHandler<ListTenantSignupsQuery, TenantSignupPage>
{
    public async Task<TenantSignupPage> Handle(ListTenantSignupsQuery query, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var rows = TenantSignupQueries.InView(db.TenantSignups.AsNoTracking(), query.Status, now);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            rows = rows.Where(s => s.InstitutionName.ToLower().Contains(term) || s.Slug.Contains(term)
                                   || s.Email.ToLower().Contains(term) || s.LastName.ToLower().Contains(term));
        }

        var (page, size) = TenantSignupHttp.Paging(query.Page, query.PageSize);
        var total = await rows.CountAsync(ct);
        rows = query.Status == TenantSignupView.Confirmed
            ? rows.OrderBy(s => s.ConfirmedAt)
            : rows.OrderByDescending(s => s.CreatedAt);
        var items = await rows.Skip((page - 1) * size).Take(size).ToListAsync(ct);

        var suffix = options.Value.ValidTemplate();
        return new TenantSignupPage(items.Select(s => TenantSignupSummary.From(s, suffix, now)).ToList(), total, page, size);
    }
}

public sealed record CountTenantSignupsQuery : IRequest<TenantSignupCounts>;

internal sealed class CountTenantSignupsHandler(AdminDbContext db, TimeProvider clock)
    : IRequestHandler<CountTenantSignupsQuery, TenantSignupCounts>
{
    public async Task<TenantSignupCounts> Handle(CountTenantSignupsQuery query, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var all = db.TenantSignups.AsNoTracking();
        Task<int> Count(TenantSignupView view) => TenantSignupQueries.InView(all, view, now).CountAsync(ct);

        return new TenantSignupCounts(
            await Count(TenantSignupView.PendingEmail), await Count(TenantSignupView.Expired),
            await Count(TenantSignupView.Confirmed), await Count(TenantSignupView.Approved),
            await Count(TenantSignupView.Rejected));
    }
}

internal static class TenantSignupQueries
{
    public static IQueryable<TenantSignup> InView(IQueryable<TenantSignup> rows, TenantSignupView? view, DateTimeOffset now) =>
        view switch
        {
            TenantSignupView.PendingEmail => rows.Where(s => s.Status == TenantSignupStatus.PendingEmail && s.TokenExpiresAt > now),
            TenantSignupView.Expired => rows.Where(s => s.Status == TenantSignupStatus.PendingEmail && s.TokenExpiresAt <= now),
            TenantSignupView.Confirmed => rows.Where(s => s.Status == TenantSignupStatus.Confirmed),
            TenantSignupView.Approved => rows.Where(s => s.Status == TenantSignupStatus.Approved),
            TenantSignupView.Rejected => rows.Where(s => s.Status == TenantSignupStatus.Rejected),
            _ => rows,
        };
}

// ── Detail ──────────────────────────────────────────────────────────────────────────────────────

public static class GetTenantSignupEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/{signupId:guid}", async (Guid signupId, ISender sender, CancellationToken ct) =>
                await sender.Send(new GetTenantSignupQuery(signupId), ct) is { } detail
                    ? Results.Ok(detail)
                    : Results.NotFound(new { error = TenantSignupErrors.NotFound }))
            .RequirePlatformAdmin()
            .WithName("GetTenantSignup")
            .WithSummary("A free-trial request, with the other requests from the same IP or the same e-mail domain")
            .Produces<TenantSignupDetail>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();
    }
}

public sealed record GetTenantSignupQuery(Guid SignupId) : IRequest<TenantSignupDetail?>;

internal sealed class GetTenantSignupHandler(AdminDbContext db, IOptions<TenantSignupOptions> options, TimeProvider clock)
    : IRequestHandler<GetTenantSignupQuery, TenantSignupDetail?>
{
    private const int RelatedMax = 10;

    public async Task<TenantSignupDetail?> Handle(GetTenantSignupQuery query, CancellationToken ct)
    {
        var signup = await db.TenantSignups.AsNoTracking().FirstOrDefaultAsync(s => s.Id == query.SignupId, ct);
        if (signup is null) return null;

        var now = clock.GetUtcNow();
        var suffix = options.Value.ValidTemplate();

        var sameIp = signup.RequestIp is null
            ? []
            : await db.TenantSignups.AsNoTracking()
                .Where(s => s.RequestIp == signup.RequestIp && s.Id != signup.Id)
                .OrderByDescending(s => s.CreatedAt).Take(RelatedMax).ToListAsync(ct);

        var domain = "@" + signup.Email[(signup.Email.LastIndexOf('@') + 1)..];
        var sameDomain = await db.TenantSignups.AsNoTracking()
            .Where(s => s.Email.EndsWith(domain) && s.Id != signup.Id)
            .OrderByDescending(s => s.CreatedAt).Take(RelatedMax).ToListAsync(ct);

        var decidedBy = signup.DecidedByAdminId is { } adminId
            ? await db.PlatformAdmins.AsNoTracking().Where(a => a.Id == adminId).Select(a => a.FullName).FirstOrDefaultAsync(ct)
            : null;

        return new TenantSignupDetail(
            TenantSignupSummary.From(signup, suffix, now), signup.RequestIp, signup.UserAgent, decidedBy,
            sameIp.Select(s => TenantSignupSummary.From(s, suffix, now)).ToList(),
            sameDomain.Select(s => TenantSignupSummary.From(s, suffix, now)).ToList());
    }
}

// ── Decisions ───────────────────────────────────────────────────────────────────────────────────

public sealed record ApprovedTenantSignup(Guid SignupId, Guid TenantId, string Fqdn, DateTimeOffset TrialExpiresAt);

public static class DecideTenantSignupEndpoints
{
    public sealed record RejectRequest(string Reason);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/{signupId:guid}/approve", async (Guid signupId, HttpContext http, ISender sender, CancellationToken ct) =>
                (await sender.Send(new ApproveTenantSignupCommand(signupId, AdminClaims.AdminId(http.User)), ct))
                .ToHttp(Results.Ok))
            .RequirePlatformAdmin()
            .WithName("ApproveTenantSignup")
            .WithSummary("Create the free-trial institution of a confirmed request")
            .WithDescription("Socle only, starting data « Starter », trial of TrialSignup:TrialDays days from now. "
                + "409 TRIAL_SIGNUP_NOT_CONFIRMED / TRIAL_SIGNUP_ALREADY_DECIDED / TRIAL_SLUG_TAKEN / TRIAL_EMAIL_IN_USE.")
            .Produces<ApprovedTenantSignup>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi();

        group.MapPost("/{signupId:guid}/reject", async (Guid signupId, RejectRequest body, HttpContext http, ISender sender,
                    CancellationToken ct) =>
                (await sender.Send(new RejectTenantSignupCommand(signupId, body.Reason, AdminClaims.AdminId(http.User)), ct))
                .ToHttp(_ => Results.NoContent()))
            .RequirePlatformAdmin()
            .WithName("RejectTenantSignup")
            .WithSummary("Refuse a free-trial request, with the reason the requester will read")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();
    }
}

public sealed record ApproveTenantSignupCommand(Guid SignupId, Guid? AdminId) : IRequest<TenantSignupResult<ApprovedTenantSignup>>;

internal sealed class ApproveTenantSignupHandler(AdminDbContext db, IOptions<TenantSignupOptions> options, TimeProvider clock)
    : IRequestHandler<ApproveTenantSignupCommand, TenantSignupResult<ApprovedTenantSignup>>
{
    public async Task<TenantSignupResult<ApprovedTenantSignup>> Handle(ApproveTenantSignupCommand command, CancellationToken ct)
    {
        if (options.Value.ValidTemplate() is not { } suffix)
            return TenantSignupResult<ApprovedTenantSignup>.Fail(TenantSignupErrors.NotConfigured);

        var signup = await db.TenantSignups.AsTracking().FirstOrDefaultAsync(s => s.Id == command.SignupId, ct);
        if (signup is null) return TenantSignupResult<ApprovedTenantSignup>.Fail(TenantSignupErrors.NotFound);
        if (signup.Status is TenantSignupStatus.Approved or TenantSignupStatus.Rejected)
            return TenantSignupResult<ApprovedTenantSignup>.Fail(TenantSignupErrors.AlreadyDecided);
        if (signup.Status != TenantSignupStatus.Confirmed)
            return TenantSignupResult<ApprovedTenantSignup>.Fail(TenantSignupErrors.NotConfirmed,
                "L'adresse e-mail de cette demande n'a pas été confirmée.");

        // Checked again: since the request, the platform may have given the address or the e-mail to
        // another institution by hand.
        var now = clock.GetUtcNow();
        if (await TenantSignupRules.SlugProblemAsync(db, signup.Slug, suffix, signup.Id, now, ct) is { } problem)
            return TenantSignupResult<ApprovedTenantSignup>.Fail(problem, "Cette adresse a été attribuée entre-temps.");
        if (await TenantSignupRules.EmailInUseAsync(db, signup.Email, signup.Id, now, ct))
            return TenantSignupResult<ApprovedTenantSignup>.Fail(TenantSignupErrors.EmailInUse,
                "Cette adresse e-mail administre déjà une institution.");

        var trialEnd = now.AddDays(options.Value.TrialDays);
        Tenant tenant;
        try
        {
            // The socle alone (profile CrmOnly), configuration-only starting data: a trial never gets
            // a paid module nor fictitious records.
            tenant = await TenantCreation.AddAsync(db, new CreateTenantCommand(
                    signup.InstitutionName, signup.Email, TenantSignupOptions.OriginFor(suffix, signup.Slug), trialEnd,
                    ActiveModules: null, command.AdminId, TenantDataLoadKind.Starter),
                TenantOffer.Trial, now, ct);
        }
        catch (InvalidOperationException)
        {
            return TenantSignupResult<ApprovedTenantSignup>.Fail(TrialSlug.Taken, "Cette adresse a été attribuée entre-temps.");
        }

        signup.Approve(tenant.Id, command.AdminId, now);
        await db.SaveChangesAsync(ct);
        return TenantSignupResult<ApprovedTenantSignup>.Ok(new ApprovedTenantSignup(signup.Id, tenant.Id, tenant.Fqdn, trialEnd));
    }
}

public sealed record RejectTenantSignupCommand(Guid SignupId, string? Reason, Guid? AdminId) : IRequest<TenantSignupResult<bool>>;

internal sealed class RejectTenantSignupHandler(AdminDbContext db, TimeProvider clock)
    : IRequestHandler<RejectTenantSignupCommand, TenantSignupResult<bool>>
{
    public async Task<TenantSignupResult<bool>> Handle(RejectTenantSignupCommand command, CancellationToken ct)
    {
        var reason = command.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > TenantSignup.ReasonMaxLength)
            return TenantSignupResult<bool>.Fail(TenantSignupErrors.ReasonRequired,
                $"Indiquez le motif du refus ({TenantSignup.ReasonMaxLength} caractères au plus).");

        var signup = await db.TenantSignups.AsTracking().FirstOrDefaultAsync(s => s.Id == command.SignupId, ct);
        if (signup is null) return TenantSignupResult<bool>.Fail(TenantSignupErrors.NotFound);
        if (signup.Status is TenantSignupStatus.Approved or TenantSignupStatus.Rejected)
            return TenantSignupResult<bool>.Fail(TenantSignupErrors.AlreadyDecided);

        signup.Reject(reason, command.AdminId, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return TenantSignupResult<bool>.Ok(true);
    }
}

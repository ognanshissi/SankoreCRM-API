using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;
using Sankore.Admin.Infrastructure.Auth;

namespace Sankore.Admin.Features.Tenants.DataWipes;

public sealed record SetTenantTestFlagRequest(bool IsTest);

/// <param name="ConfirmationName">The institution's name, retyped: a wipe is irreversible.</param>
public sealed record RequestDataWipeRequest(string ConfirmationName);

public sealed record TenantDataWipeResponse(
    Guid Id, Guid TenantId, Guid RequestedBy, DateTimeOffset RequestedAt, string Status,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Summary, string? Error);

public sealed record PendingDataWipeResponse(Guid Id, Guid TenantId);

public sealed record CompleteDataWipeRequest(string? Summary);

public sealed record FailDataWipeRequest(string Error);

/// <summary>
/// Emptying a TEST institution of its business data (data-purge lot 4, owner's choices of
/// 2026-10-24): only from this platform, only a platform administrator, only an institution
/// marked as a test one, and only after retyping its name. The API pulls the pending wipes with
/// its service key and reports back; it cannot request one.
/// </summary>
public static class DataWipeEndpoints
{
    public const string NotTest = "TENANT_NOT_TEST";
    public const string ConfirmationMismatch = "CONFIRMATION_MISMATCH";
    public const string InFlight = "DATA_WIPE_IN_FLIGHT";
    public const string NotPending = "DATA_WIPE_NOT_PENDING";
    public const string NotRunning = "DATA_WIPE_FINISHED";

    public static void MapDataWipeEndpoints(this WebApplication app)
    {
        // ── The platform administrator ────────────────────────────────────────
        var tenant = app.MapGroup("/api/v1/tenants/{tenantId:guid}").WithTags("Tenant data wipes");

        tenant.MapPut("test-flag", async (Guid tenantId, SetTenantTestFlagRequest body, AdminDbContext db, ILoggerFactory logs, CancellationToken ct) =>
            {
                var found = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
                if (found is null) return Results.NotFound();
                found.MarkAsTest(body.IsTest);
                await db.SaveChangesAsync(ct);
                logs.CreateLogger("DataWipes").LogWarning("Tenant {TenantId} ({Name}) marked IsTest={IsTest}", found.Id, found.Name, body.IsTest);
                return Results.NoContent();
            })
            .RequirePlatformAdmin()
            .WithName("SetTenantTestFlag")
            .WithSummary("Mark an institution as a recette/demo one — the only kind whose data may be wiped")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        tenant.MapPost("data-wipes", async (Guid tenantId, RequestDataWipeRequest body, HttpContext http, AdminDbContext db, ILoggerFactory logs, CancellationToken ct) =>
            {
                var (error, wipe) = await DataWipeRules.RequestAsync(
                    db, tenantId, body.ConfirmationName, AdminClaims.AdminId(http.User) ?? Guid.Empty, DateTimeOffset.UtcNow, ct);
                return error switch
                {
                    null => Ok(wipe!, logs),
                    NotFound => Results.NotFound(),
                    ConfirmationMismatch => Results.BadRequest(new { error }),
                    _ => Results.Conflict(new { error }),
                };
            })
            .RequirePlatformAdmin()
            .WithName("RequestTenantDataWipe")
            .WithSummary("Empty a TEST institution of its business data; its configuration and users stay")
            .Produces<TenantDataWipeResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();

        tenant.MapGet("data-wipes", async (Guid tenantId, AdminDbContext db, CancellationToken ct) =>
                Results.Ok((await db.TenantDataWipes.AsNoTracking()
                        .Where(w => w.TenantId == tenantId)
                        .OrderByDescending(w => w.RequestedAt)
                        .Take(20)
                        .ToListAsync(ct))
                    .Select(ToResponse)))
            .RequirePlatformAdmin()
            .WithName("ListTenantDataWipes")
            .Produces<IEnumerable<TenantDataWipeResponse>>()
            .WithOpenApi();

        // ── The API, with its service key ─────────────────────────────────────
        var service = app.MapGroup("/api/v1/data-wipes").WithTags("Tenant data wipes");

        service.MapGet("pending", async (AdminDbContext db, CancellationToken ct) =>
                Results.Ok(await db.TenantDataWipes.AsNoTracking()
                    .Where(w => w.Status == TenantDataWipeStatus.Pending)
                    .OrderBy(w => w.RequestedAt)
                    .Select(w => new PendingDataWipeResponse(w.Id, w.TenantId))
                    .ToListAsync(ct)))
            .WithName("ListPendingDataWipes")
            .Produces<IEnumerable<PendingDataWipeResponse>>()
            .WithOpenApi();

        service.MapPost("{wipeId:guid}/start", async (Guid wipeId, AdminDbContext db, CancellationToken ct) =>
            {
                // One conditional UPDATE, not read-then-save: two API instances polling at the same
                // second must not both get a yes and wipe twice. The row count is the answer.
                var now = DateTimeOffset.UtcNow;
                var taken = await db.TenantDataWipes
                    .Where(w => w.Id == wipeId && w.Status == TenantDataWipeStatus.Pending)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(w => w.Status, TenantDataWipeStatus.Running)
                        .SetProperty(w => w.StartedAt, now), ct);
                return taken == 1 ? Results.NoContent() : Results.Conflict(new { error = NotPending });
            })
            .WithName("StartDataWipe")
            .WithOpenApi();

        // A finished wipe is a record: only a RUNNING one completes, only an open one fails — one
        // conditional UPDATE each, so a replayed or late call cannot rewrite what happened.
        // (The whole host is behind the service key or a platform administrator — Program.cs.)
        service.MapPost("{wipeId:guid}/complete", async (Guid wipeId, CompleteDataWipeRequest body, AdminDbContext db, CancellationToken ct) =>
            {
                var now = DateTimeOffset.UtcNow;
                var done = await db.TenantDataWipes
                    .Where(w => w.Id == wipeId && w.Status == TenantDataWipeStatus.Running)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(w => w.Status, TenantDataWipeStatus.Completed)
                        .SetProperty(w => w.Summary, body.Summary)
                        .SetProperty(w => w.FinishedAt, now), ct);
                return done == 1 ? Results.NoContent() : Results.Conflict(new { error = NotRunning });
            })
            .WithName("CompleteDataWipe")
            .WithOpenApi();

        service.MapPost("{wipeId:guid}/fail", async (Guid wipeId, FailDataWipeRequest body, AdminDbContext db, CancellationToken ct) =>
            {
                var now = DateTimeOffset.UtcNow;
                var error = body.Error.Length > 2000 ? body.Error[..2000] : body.Error;
                var done = await db.TenantDataWipes
                    .Where(w => w.Id == wipeId
                             && (w.Status == TenantDataWipeStatus.Pending || w.Status == TenantDataWipeStatus.Running))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(w => w.Status, TenantDataWipeStatus.Failed)
                        .SetProperty(w => w.Error, error)
                        .SetProperty(w => w.FinishedAt, now), ct);
                return done == 1 ? Results.NoContent() : Results.Conflict(new { error = NotRunning });
            })
            .WithName("FailDataWipe")
            .WithOpenApi();
    }

    public const string NotFound = "TENANT_NOT_FOUND";

    private static IResult Ok(TenantDataWipe wipe, ILoggerFactory logs)
    {
        logs.CreateLogger("DataWipes").LogWarning(
            "Data wipe {WipeId} requested for TEST tenant {TenantId} by platform admin {AdminId}",
            wipe.Id, wipe.TenantId, wipe.RequestedBy);
        return Results.Ok(ToResponse(wipe));
    }

    private static TenantDataWipeResponse ToResponse(TenantDataWipe w) => new(
        w.Id, w.TenantId, w.RequestedBy, w.RequestedAt, w.Status.ToString(), w.StartedAt, w.FinishedAt, w.Summary, w.Error);
}

/// <summary>The three refusals of a wipe request, in one testable place.</summary>
internal static class DataWipeRules
{
    public static async Task<(string? Error, TenantDataWipe? Wipe)> RequestAsync(
        AdminDbContext db, Guid tenantId, string? confirmationName, Guid requestedBy, DateTimeOffset now, CancellationToken ct)
    {
        var found = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (found is null) return (DataWipeEndpoints.NotFound, null);
        if (!found.IsTest) return (DataWipeEndpoints.NotTest, null);

        // Exact, after trimming: the point is that the operator READ which institution it is.
        if (!string.Equals(confirmationName?.Trim(), found.Name.Trim(), StringComparison.Ordinal))
            return (DataWipeEndpoints.ConfirmationMismatch, null);

        var inFlight = await db.TenantDataWipes.AnyAsync(w => w.TenantId == tenantId
            && (w.Status == TenantDataWipeStatus.Pending || w.Status == TenantDataWipeStatus.Running), ct);
        if (inFlight) return (DataWipeEndpoints.InFlight, null);

        var wipe = TenantDataWipe.Request(tenantId, requestedBy, now);
        db.TenantDataWipes.Add(wipe);
        await db.SaveChangesAsync(ct);
        return (null, wipe);
    }
}

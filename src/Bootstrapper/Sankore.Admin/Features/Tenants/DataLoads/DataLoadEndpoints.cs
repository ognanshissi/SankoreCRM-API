using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;
using Sankore.Admin.Infrastructure.Auth;

namespace Sankore.Admin.Features.Tenants.DataLoads;

/// <param name="Kind"><c>Starter</c> or <c>Demo</c>.</param>
public sealed record RequestDataLoadRequest(string Kind);

public sealed record TenantDataLoadResponse(
    Guid Id, Guid TenantId, string Kind, Guid RequestedBy, DateTimeOffset RequestedAt, string Status,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Summary, string? Error);

public sealed record PendingDataLoadResponse(Guid Id, Guid TenantId, string Kind);

public sealed record CompleteDataLoadRequest(string? Summary);

public sealed record FailDataLoadRequest(string Error);

/// <summary>
/// Loading an institution's starting data (<c>docs/donnees-demarrage-demo.md</c>, owner's choices of
/// 2026-11-08): requested only from this platform by a platform administrator, pulled and run by the
/// API with its service key, which reports back — the data wipe's circuit, in the other direction.
/// </summary>
public static class DataLoadEndpoints
{
    public const string NotFound = "TENANT_NOT_FOUND";
    public const string KindUnknown = "DATA_LOAD_KIND_UNKNOWN";
    public const string DemoNeedsTest = "DEMO_REQUIRES_TEST_TENANT";
    public const string Twin = "DATA_LOAD_ON_TEST_TWIN";
    public const string InFlight = "DATA_LOAD_IN_FLIGHT";
    public const string NotPending = "DATA_LOAD_NOT_PENDING";
    public const string NotRunning = "DATA_LOAD_FINISHED";

    public static void MapDataLoadEndpoints(this WebApplication app)
    {
        // ── The platform administrator ────────────────────────────────────────
        var tenant = app.MapGroup("/api/v1/tenants/{tenantId:guid}").WithTags("Tenant data loads");

        tenant.MapPost("data-loads", async (Guid tenantId, RequestDataLoadRequest body, HttpContext http, AdminDbContext db, ILoggerFactory logs, CancellationToken ct) =>
            {
                var (error, load) = await DataLoadRules.RequestAsync(
                    db, tenantId, body.Kind, AdminClaims.AdminId(http.User) ?? Guid.Empty, DateTimeOffset.UtcNow, ct);
                if (error is null)
                {
                    logs.CreateLogger("DataLoads").LogInformation(
                        "{Kind} data load {LoadId} requested for tenant {TenantId} by platform admin {AdminId}",
                        load!.Kind, load.Id, load.TenantId, load.RequestedBy);
                    return Results.Ok(ToResponse(load));
                }
                return error switch
                {
                    NotFound => Results.NotFound(),
                    KindUnknown => Results.BadRequest(new { error }),
                    _ => Results.Conflict(new { error }),
                };
            })
            .RequirePlatformAdmin()
            .WithName("RequestTenantDataLoad")
            .WithSummary("Load an institution's starting data: Starter (configuration) or Demo (test institutions only)")
            .Produces<TenantDataLoadResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();

        tenant.MapGet("data-loads", async (Guid tenantId, AdminDbContext db, CancellationToken ct) =>
                Results.Ok((await db.TenantDataLoads.AsNoTracking()
                        .Where(l => l.TenantId == tenantId)
                        .OrderByDescending(l => l.RequestedAt)
                        .Take(20)
                        .ToListAsync(ct))
                    .Select(ToResponse)))
            .RequirePlatformAdmin()
            .WithName("ListTenantDataLoads")
            .Produces<IEnumerable<TenantDataLoadResponse>>()
            .WithOpenApi();

        // ── The API, with its service key ─────────────────────────────────────
        var service = app.MapGroup("/api/v1/data-loads").WithTags("Tenant data loads");

        service.MapGet("pending", async (AdminDbContext db, CancellationToken ct) =>
                Results.Ok((await db.TenantDataLoads.AsNoTracking()
                        .Where(l => l.Status == TenantDataLoadStatus.Pending)
                        .OrderBy(l => l.RequestedAt)
                        .ToListAsync(ct))
                    .Select(l => new PendingDataLoadResponse(l.Id, l.TenantId, l.Kind.ToString()))))
            .WithName("ListPendingDataLoads")
            .Produces<IEnumerable<PendingDataLoadResponse>>()
            .WithOpenApi();

        // Conditional UPDATEs, as for the wipes: two API instances cannot both take a load, and a
        // finished one is never rewritten.
        service.MapPost("{loadId:guid}/start", async (Guid loadId, AdminDbContext db, CancellationToken ct) =>
            {
                var now = DateTimeOffset.UtcNow;
                var taken = await db.TenantDataLoads
                    .Where(l => l.Id == loadId && l.Status == TenantDataLoadStatus.Pending)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(l => l.Status, TenantDataLoadStatus.Running)
                        .SetProperty(l => l.StartedAt, now), ct);
                return taken == 1 ? Results.NoContent() : Results.Conflict(new { error = NotPending });
            })
            .WithName("StartDataLoad")
            .WithOpenApi();

        service.MapPost("{loadId:guid}/complete", async (Guid loadId, CompleteDataLoadRequest body, AdminDbContext db, CancellationToken ct) =>
            {
                var now = DateTimeOffset.UtcNow;
                var done = await db.TenantDataLoads
                    .Where(l => l.Id == loadId && l.Status == TenantDataLoadStatus.Running)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(l => l.Status, TenantDataLoadStatus.Completed)
                        .SetProperty(l => l.Summary, body.Summary)
                        .SetProperty(l => l.FinishedAt, now), ct);
                return done == 1 ? Results.NoContent() : Results.Conflict(new { error = NotRunning });
            })
            .WithName("CompleteDataLoad")
            .WithOpenApi();

        service.MapPost("{loadId:guid}/fail", async (Guid loadId, FailDataLoadRequest body, AdminDbContext db, CancellationToken ct) =>
            {
                var now = DateTimeOffset.UtcNow;
                var error = body.Error.Length > 2000 ? body.Error[..2000] : body.Error;
                var done = await db.TenantDataLoads
                    .Where(l => l.Id == loadId
                             && (l.Status == TenantDataLoadStatus.Pending || l.Status == TenantDataLoadStatus.Running))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(l => l.Status, TenantDataLoadStatus.Failed)
                        .SetProperty(l => l.Error, error)
                        .SetProperty(l => l.FinishedAt, now), ct);
                return done == 1 ? Results.NoContent() : Results.Conflict(new { error = NotRunning });
            })
            .WithName("FailDataLoad")
            .WithOpenApi();
    }

    private static TenantDataLoadResponse ToResponse(TenantDataLoad l) => new(
        l.Id, l.TenantId, l.Kind.ToString(), l.RequestedBy, l.RequestedAt, l.Status.ToString(),
        l.StartedAt, l.FinishedAt, l.Summary, l.Error);
}

/// <summary>The refusals of a load request, in one testable place — shared with the tenant creation.</summary>
internal static class DataLoadRules
{
    public static bool TryParseKind(string? kind, out TenantDataLoadKind parsed)
        => Enum.TryParse(kind?.Trim(), ignoreCase: true, out parsed) && Enum.IsDefined(parsed);

    /// <summary>
    /// Why <paramref name="tenant"/> may not receive <paramref name="kind"/>, or null. A « Mode test »
    /// twin never: its configuration comes from production (« Repartir de la production ») and its team
    /// IS production's, so fictitious leads would be dispatched to real agents. The demo only on a test
    /// institution: fictitious accounts and records must never land in a real one.
    /// </summary>
    public static string? Refusal(Tenant tenant, TenantDataLoadKind kind)
        => tenant.ParentTenantId is not null ? DataLoadEndpoints.Twin
         : kind == TenantDataLoadKind.Demo && !tenant.IsTest ? DataLoadEndpoints.DemoNeedsTest
         : null;

    public static async Task<(string? Error, TenantDataLoad? Load)> RequestAsync(
        AdminDbContext db, Guid tenantId, string? kind, Guid requestedBy, DateTimeOffset now, CancellationToken ct)
    {
        if (!TryParseKind(kind, out var parsed)) return (DataLoadEndpoints.KindUnknown, null);

        var found = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (found is null) return (DataLoadEndpoints.NotFound, null);
        if (Refusal(found, parsed) is { } refusal) return (refusal, null);

        var inFlight = await db.TenantDataLoads.AnyAsync(l => l.TenantId == tenantId
            && (l.Status == TenantDataLoadStatus.Pending || l.Status == TenantDataLoadStatus.Running), ct);
        if (inFlight) return (DataLoadEndpoints.InFlight, null);

        var load = TenantDataLoad.Request(tenantId, parsed, requestedBy, now);
        db.TenantDataLoads.Add(load);
        await db.SaveChangesAsync(ct);
        return (null, load);
    }
}

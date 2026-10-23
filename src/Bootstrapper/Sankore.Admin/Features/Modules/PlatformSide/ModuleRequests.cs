using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;
using Sankore.Admin.Infrastructure.Auth;

namespace Sankore.Admin.Features.Modules.PlatformSide;

/// <summary>A subscription row across tenants, with the names an operator needs to read it.</summary>
public sealed record ModuleSubscriptionListItem(
    TenantModuleSubscriptionResponse Subscription,
    string TenantName,
    string ModuleLabel);

/// <summary>
/// The platform's queue (status Requested, oldest first) and, with another status, its view of
/// what tenants did — Deactivated is how the platform learns a tenant switched a module off.
/// </summary>
public static class ListModuleSubscriptionsEndpoint
{
    public const int MaxItems = 200;

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/", async ([FromQuery] ModuleSubscriptionStatus? status, [FromQuery] Guid? tenantId,
                    ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(
                    new ListModuleSubscriptionsQuery(status ?? ModuleSubscriptionStatus.Requested, tenantId), ct)))
            .RequirePlatformAdmin()
            .WithName("ListModuleSubscriptions")
            .WithSummary("Module subscriptions across tenants, by status (Requested by default)")
            .Produces<List<ModuleSubscriptionListItem>>()
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();
    }
}

public sealed record ListModuleSubscriptionsQuery(ModuleSubscriptionStatus Status, Guid? TenantId)
    : IRequest<List<ModuleSubscriptionListItem>>;

internal sealed class ListModuleSubscriptionsHandler(AdminDbContext db)
    : IRequestHandler<ListModuleSubscriptionsQuery, List<ModuleSubscriptionListItem>>
{
    public async Task<List<ModuleSubscriptionListItem>> Handle(ListModuleSubscriptionsQuery query, CancellationToken ct)
    {
        var rows = db.TenantModuleSubscriptions.AsNoTracking().Where(s => s.Status == query.Status);
        if (query.TenantId is { } tenantId) rows = rows.Where(s => s.TenantId == tenantId);

        // A queue is served oldest first; a history is read newest first.
        rows = query.Status == ModuleSubscriptionStatus.Requested
            ? rows.OrderBy(s => s.CreatedAt)
            : rows.OrderByDescending(s => s.CreatedAt);

        var page = await rows.Take(ListModuleSubscriptionsEndpoint.MaxItems).ToListAsync(ct);

        var tenantIds = page.Select(r => r.TenantId).Distinct().ToList();
        var tenantNames = await db.Tenants.AsNoTracking()
            .Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var labels = await db.ModuleDefinitions.AsNoTracking()
            .ToDictionaryAsync(m => m.Code, m => m.Label, ct);

        return page.Select(r => new ModuleSubscriptionListItem(
                TenantModuleSubscriptionResponse.From(r),
                tenantNames.GetValueOrDefault(r.TenantId, "—"),
                labels.GetValueOrDefault(r.ModuleCode, r.ModuleCode)))
            .ToList();
    }
}

public sealed record RejectModuleRequestRequest(string Reason);

public static class DecideModuleRequestEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/{subscriptionId:guid}/approve", async (Guid subscriptionId, HttpContext http, ISender sender,
                    CancellationToken ct) =>
                (await sender.Send(new DecideModuleRequestCommand(subscriptionId, AdminClaims.AdminId(http.User)!.Value,
                    Approve: true, Reason: null), ct))
                .ToHttp(ModuleHttp.NoContent))
            .RequirePlatformAdmin()
            .WithName("ApproveModuleRequest")
            .WithSummary("Authorise a tenant's module request; the module is active within the API's cache delay")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();

        group.MapPost("/{subscriptionId:guid}/reject", async (Guid subscriptionId, RejectModuleRequestRequest body, HttpContext http,
                    ISender sender, CancellationToken ct) =>
                (await sender.Send(new DecideModuleRequestCommand(subscriptionId, AdminClaims.AdminId(http.User)!.Value,
                    Approve: false, Reason: body.Reason), ct))
                .ToHttp(ModuleHttp.NoContent))
            .RequirePlatformAdmin()
            .WithName("RejectModuleRequest")
            .WithSummary("Refuse a tenant's module request; the reason is shown to the tenant")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();
    }
}

public sealed record DecideModuleRequestCommand(Guid SubscriptionId, Guid AdminId, bool Approve, string? Reason)
    : IRequest<ModuleResult>;

internal sealed class DecideModuleRequestHandler(AdminDbContext db, ILogger<DecideModuleRequestHandler> logger)
    : IRequestHandler<DecideModuleRequestCommand, ModuleResult>
{
    public async Task<ModuleResult> Handle(DecideModuleRequestCommand cmd, CancellationToken ct)
    {
        if (!cmd.Approve && (string.IsNullOrWhiteSpace(cmd.Reason)
                             || cmd.Reason.Trim().Length > TenantModuleSubscription.ReasonMaxLength))
            return ModuleResult.Fail(ModuleErrors.RejectionReasonRequired);

        var row = await db.TenantModuleSubscriptions.FirstOrDefaultAsync(s => s.Id == cmd.SubscriptionId, ct);
        if (row is null) return ModuleResult.Fail(ModuleErrors.SubscriptionNotFound);

        var now = DateTimeOffset.UtcNow;
        var moved = cmd.Approve ? row.Approve(cmd.AdminId, now) : row.Reject(cmd.AdminId, cmd.Reason!, now);
        if (!moved) return ModuleResult.Fail(ModuleErrors.RequestNotPending);

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Module request {SubscriptionId} ({Module}, tenant {TenantId}) {Decision} by {AdminId}",
            row.Id, row.ModuleCode, row.TenantId, cmd.Approve ? "approved" : "rejected", cmd.AdminId);
        return ModuleResult.Ok(row.Id);
    }
}

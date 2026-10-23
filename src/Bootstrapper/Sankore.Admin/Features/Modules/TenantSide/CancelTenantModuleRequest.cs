using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.Modules.TenantSide;

/// <summary>The tenant withdraws a request the platform has not decided yet.</summary>
public static class CancelTenantModuleRequestEndpoint
{
    public sealed record Request(Guid UserId);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/requests/{subscriptionId:guid}/cancel", async (Guid tenantId, Guid subscriptionId,
                    Request body, ISender sender, CancellationToken ct) =>
                (await sender.Send(new CancelTenantModuleRequestCommand(tenantId, subscriptionId, body.UserId), ct))
                .ToHttp(ModuleHttp.NoContent))
            .WithName("CancelTenantModuleRequest")
            .WithSummary("Withdraw a pending module request")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();
    }
}

public sealed record CancelTenantModuleRequestCommand(Guid TenantId, Guid SubscriptionId, Guid UserId)
    : IRequest<ModuleResult>;

internal sealed class CancelTenantModuleRequestHandler(AdminDbContext db, ILogger<CancelTenantModuleRequestHandler> logger)
    : IRequestHandler<CancelTenantModuleRequestCommand, ModuleResult>
{
    public async Task<ModuleResult> Handle(CancelTenantModuleRequestCommand cmd, CancellationToken ct)
    {
        if (cmd.UserId == Guid.Empty) return ModuleResult.Fail(ModuleErrors.InvalidActor);

        // Scoped by tenant: a row id from another tenant reads as absent, never as someone else's.
        var row = await db.TenantModuleSubscriptions
            .FirstOrDefaultAsync(s => s.Id == cmd.SubscriptionId && s.TenantId == cmd.TenantId, ct);
        if (row is null) return ModuleResult.Fail(ModuleErrors.SubscriptionNotFound);

        if (!row.Cancel(cmd.UserId, DateTimeOffset.UtcNow))
            return ModuleResult.Fail(ModuleErrors.RequestNotPending);

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Tenant {TenantId} cancelled its request for module {Module}", cmd.TenantId, row.ModuleCode);
        return ModuleResult.Ok(row.Id);
    }
}

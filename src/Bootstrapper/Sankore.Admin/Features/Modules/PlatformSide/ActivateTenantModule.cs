using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;
using Sankore.Admin.Infrastructure.Auth;

namespace Sankore.Admin.Features.Modules.PlatformSide;

/// <summary>
/// The platform switches a module on without waiting for a request — after a sale, typically.
/// A pending request for the same module is approved rather than duplicated. Availability and
/// price are not checked: the platform is the one that sets them.
/// </summary>
public static class ActivateTenantModuleEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/{module}/activate", async (Guid tenantId, string module, HttpContext http, ISender sender,
                    CancellationToken ct) =>
                (await sender.Send(new ActivateTenantModuleCommand(tenantId, module, AdminClaims.AdminId(http.User)!.Value), ct))
                .ToHttp(ModuleHttp.NoContent))
            .RequirePlatformAdmin()
            .WithName("ActivateTenantModule")
            .WithSummary("Switch a module on for a tenant (platform administrators only)")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();
    }
}

public sealed record ActivateTenantModuleCommand(Guid TenantId, string Module, Guid AdminId) : IRequest<ModuleResult>;

internal sealed class ActivateTenantModuleHandler(AdminDbContext db, ILogger<ActivateTenantModuleHandler> logger)
    : IRequestHandler<ActivateTenantModuleCommand, ModuleResult>
{
    public async Task<ModuleResult> Handle(ActivateTenantModuleCommand cmd, CancellationToken ct)
    {
        if (!await db.ModuleDefinitions.AnyAsync(m => m.Code == cmd.Module, ct))
            return ModuleResult.Fail(ModuleErrors.ModuleUnknown);

        if (!await db.Tenants.AnyAsync(t => t.Id == cmd.TenantId, ct))
            return ModuleResult.Fail(ModuleErrors.TenantNotFound);

        var now = DateTimeOffset.UtcNow;
        var open = await db.TenantModuleSubscriptions.FindOpenAsync(cmd.TenantId, cmd.Module, ct);

        TenantModuleSubscription row;
        if (open is null)
        {
            row = TenantModuleSubscription.ActivateDirect(cmd.TenantId, cmd.Module, cmd.AdminId,
                ModuleSubscriptionSource.Direct, now);
            db.TenantModuleSubscriptions.Add(row);
        }
        else if (open.Approve(cmd.AdminId, now))
        {
            row = open;
        }
        else
        {
            return ModuleResult.Fail(ModuleErrors.AlreadyActive);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ModuleQueries.IsOpenRowViolation(ex))
        {
            return ModuleResult.Fail(ModuleErrors.AlreadyActive);
        }

        logger.LogInformation("Module {Module} activated for tenant {TenantId} by platform admin {AdminId}",
            cmd.Module, cmd.TenantId, cmd.AdminId);
        return ModuleResult.Ok(row.Id);
    }
}

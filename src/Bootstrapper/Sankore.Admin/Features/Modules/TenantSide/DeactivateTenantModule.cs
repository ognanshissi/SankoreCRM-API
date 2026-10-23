using MediatR;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.Modules.TenantSide;

/// <summary>
/// The tenant administrator switches a module off. Immediate, with no platform approval — it
/// lowers the tenant's bill and risks nothing for the platform — and traced: the row keeps who
/// and when, which ends the billed period, and the platform reads it in its subscription list.
/// No data is touched anywhere; switching the module back on finds everything where it was.
/// </summary>
public static class DeactivateTenantModuleEndpoint
{
    public sealed record Request(Guid UserId, string Email);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/{module}/deactivate", async (Guid tenantId, string module, Request body, ISender sender,
                    CancellationToken ct) =>
                (await sender.Send(new DeactivateTenantModuleCommand(tenantId, module, body.UserId, body.Email), ct))
                .ToHttp(ModuleHttp.NoContent))
            .WithName("DeactivateTenantModule")
            .WithSummary("Switch a module off, effective immediately")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();
    }
}

public sealed record DeactivateTenantModuleCommand(Guid TenantId, string Module, Guid UserId, string Email)
    : IRequest<ModuleResult>;

internal sealed class DeactivateTenantModuleHandler(AdminDbContext db, ILogger<DeactivateTenantModuleHandler> logger)
    : IRequestHandler<DeactivateTenantModuleCommand, ModuleResult>
{
    public async Task<ModuleResult> Handle(DeactivateTenantModuleCommand cmd, CancellationToken ct)
    {
        if (cmd.UserId == Guid.Empty || string.IsNullOrWhiteSpace(cmd.Email)
            || cmd.Email.Length > TenantModuleSubscription.EmailMaxLength)
            return ModuleResult.Fail(ModuleErrors.InvalidActor);

        var open = await db.TenantModuleSubscriptions.FindOpenAsync(cmd.TenantId, cmd.Module, ct);
        if (open is null || !open.Deactivate(cmd.UserId, cmd.Email.Trim(), DateTimeOffset.UtcNow))
            return ModuleResult.Fail(ModuleErrors.NotActive);

        await db.SaveChangesAsync(ct);

        // Warning, like a tenant deactivation: the platform loses revenue here, and an operator
        // scanning the logs should see it without a query.
        logger.LogWarning("Tenant {TenantId} deactivated module {Module} ({Email})",
            cmd.TenantId, cmd.Module, open.DeactivatedByEmail);
        return ModuleResult.Ok(open.Id);
    }
}

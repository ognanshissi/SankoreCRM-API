using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.Tenants.SetTenantActivation;

/// <summary>
/// Cuts or restores a tenant's access, and nothing else.
///
/// <para>
/// <c>PUT /tenants/{id}</c> could already do it, but only by resending the name and the origin
/// with it: a screen that merely wants to switch a tenant off would have to round-trip fields it
/// does not edit, and a stale copy would silently rename the tenant or move its origin.
/// </para>
///
/// <para>
/// Sankore.Api enforces the flag (<c>TenantResolutionMiddleware</c>, 403 <c>TENANT_INACTIVE</c>)
/// and re-reads a tenant's status every <c>TenantStore:CacheTtl</c> (30 s by default), so a
/// deactivation reaches open sessions within that delay.
/// </para>
/// </summary>
public static class SetTenantActivationEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/{id:guid}/deactivate", (Guid id, ISender sender, CancellationToken ct) =>
                Handle(sender.Send(new SetTenantActivationCommand(id, false), ct)))
            .WithName("DeactivateTenant")
            .WithSummary("Cut a tenant's access")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        group.MapPost("/{id:guid}/activate", (Guid id, ISender sender, CancellationToken ct) =>
                Handle(sender.Send(new SetTenantActivationCommand(id, true), ct)))
            .WithName("ActivateTenant")
            .WithSummary("Restore a tenant's access")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();
    }

    private static async Task<IResult> Handle(Task<bool> found) =>
        await found ? Results.NoContent() : Results.NotFound();
}

public sealed record SetTenantActivationCommand(Guid Id, bool IsActive) : IRequest<bool>;

internal sealed class SetTenantActivationHandler(AdminDbContext db, ILogger<SetTenantActivationHandler> logger)
    : IRequestHandler<SetTenantActivationCommand, bool>
{
    public async Task<bool> Handle(SetTenantActivationCommand request, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == request.Id, ct);
        if (tenant is null) return false;

        // Idempotent: Deactivate() stamps BlockedAt, and a second click must not move the date
        // the access was actually cut.
        if (tenant.IsActive == request.IsActive) return true;

        if (request.IsActive) tenant.Activate();
        else tenant.Deactivate();

        await db.SaveChangesAsync(ct);
        logger.LogWarning("Tenant {TenantId} ({TenantName}) {Action}",
            tenant.Id, tenant.Name, request.IsActive ? "reactivated" : "deactivated");
        return true;
    }
}

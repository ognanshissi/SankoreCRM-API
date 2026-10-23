using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.Modules.TenantSide;

/// <summary>
/// A tenant administrator asks for a paid module. Sankore.Api calls it with the service key, on
/// behalf of the signed-in user it names: the registry has no user of its own to check, and the
/// API derives the tenant and the user from its own token, never from its caller's input.
/// </summary>
public static class RequestTenantModuleEndpoint
{
    /// <param name="AcceptedMonthlyPrice">The price the user was shown and accepted.</param>
    public sealed record Request(Guid UserId, string Email, decimal AcceptedMonthlyPrice, string AcceptedCurrency);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/{module}/requests", async (Guid tenantId, string module, Request body, ISender sender,
                    CancellationToken ct) =>
                (await sender.Send(new RequestTenantModuleCommand(tenantId, module, body.UserId, body.Email,
                    body.AcceptedMonthlyPrice, body.AcceptedCurrency), ct))
                .ToHttp(r => Results.Created($"/api/v1/tenants/{tenantId}/modules", new { id = r.SubscriptionId })))
            .WithName("RequestTenantModule")
            .WithSummary("Ask for a paid module on a tenant's behalf")
            .WithDescription("Refused with MODULE_PRICE_CHANGED when the accepted price is no longer the "
                + "current one, MODULE_UNAVAILABLE while the module is not offered, MODULE_REQUEST_PENDING "
                + "or MODULE_ALREADY_ACTIVE when a row is already open.")
            .Produces(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();
    }
}

public sealed record RequestTenantModuleCommand(
    Guid TenantId, string Module, Guid UserId, string Email, decimal AcceptedMonthlyPrice, string AcceptedCurrency)
    : IRequest<ModuleResult>;

internal sealed class RequestTenantModuleHandler(AdminDbContext db, ILogger<RequestTenantModuleHandler> logger)
    : IRequestHandler<RequestTenantModuleCommand, ModuleResult>
{
    public async Task<ModuleResult> Handle(RequestTenantModuleCommand cmd, CancellationToken ct)
    {
        if (cmd.UserId == Guid.Empty || string.IsNullOrWhiteSpace(cmd.Email)
            || cmd.Email.Length > TenantModuleSubscription.EmailMaxLength)
            return ModuleResult.Fail(ModuleErrors.InvalidActor);

        var definition = await db.ModuleDefinitions.FirstOrDefaultAsync(m => m.Code == cmd.Module, ct);
        if (definition is null) return ModuleResult.Fail(ModuleErrors.ModuleUnknown);

        if (!await db.Tenants.AnyAsync(t => t.Id == cmd.TenantId, ct))
            return ModuleResult.Fail(ModuleErrors.TenantNotFound);

        var open = await db.TenantModuleSubscriptions.FindOpenAsync(cmd.TenantId, cmd.Module, ct);
        if (open is not null) return ModuleResult.Fail(ModuleQueries.ConflictFor(open.Status));

        if (!definition.IsAvailable) return ModuleResult.Fail(ModuleErrors.ModuleUnavailable);

        if (!definition.MatchesPrice(cmd.AcceptedMonthlyPrice, cmd.AcceptedCurrency))
            return ModuleResult.Fail(ModuleErrors.PriceChanged,
                $"Current price: {definition.MonthlyPrice} {definition.Currency}.");

        var row = TenantModuleSubscription.Request(cmd.TenantId, cmd.Module, cmd.UserId, cmd.Email.Trim(),
            cmd.AcceptedMonthlyPrice, cmd.AcceptedCurrency, DateTimeOffset.UtcNow);
        db.TenantModuleSubscriptions.Add(row);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ModuleQueries.IsOpenRowViolation(ex))
        {
            db.Entry(row).State = EntityState.Detached;
            var winner = await db.TenantModuleSubscriptions.AsNoTracking().FindOpenAsync(cmd.TenantId, cmd.Module, ct);
            return ModuleResult.Fail(winner is null ? ModuleErrors.RequestPending : ModuleQueries.ConflictFor(winner.Status));
        }

        logger.LogInformation(
            "Tenant {TenantId} requested module {Module} at {Price} {Currency} ({Email})",
            cmd.TenantId, cmd.Module, row.AcceptedMonthlyPrice, row.AcceptedCurrency, row.RequestedByEmail);
        return ModuleResult.Ok(row.Id);
    }
}

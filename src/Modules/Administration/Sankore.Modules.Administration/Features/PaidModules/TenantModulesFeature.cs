namespace Sankore.Modules.Administration.Features.PaidModules;

using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.PaidModules;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;

// ── Area aggregator ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The tenant administrator's side of the paid modules (« Banking - Microfinance »,
/// « Assurance »): read the offer and where the tenant stands, ask for a module (the platform
/// authorises, because each one has a cost), withdraw a pending request, switch a module off
/// (immediate). The registry (Sankore.Admin) holds the truth; this area relays to it with the
/// service key, always for the caller's own tenant and in the caller's own name.
/// </summary>
internal static class TenantModulesEndpoints
{
    public static IEndpointRouteBuilder MapTenantModulesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("tenant-modules").WithTags("Tenant Modules");

        group.MapGet("", async (ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new ListTenantModulesQuery(), ct);
                return result.IsSuccess ? Results.Ok(result.Value) : TenantModulesErrors.ToProblem(result.Error!);
            })
            .WithName("ListTenantModules")
            .WithSummary("The paid modules: offer, price, the tenant's state and history")
            .RequireAuthorization(Permissions.CanReadTenantModules.Code)
            .Produces<TenantModulesDto>()
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{module}/requests", async (string module, RequestTenantModuleBody body, ISender sender,
                    CancellationToken ct) =>
            {
                var result = await sender.Send(new RequestTenantModuleCommand(module, body.AcceptedMonthlyPrice,
                    body.AcceptedCurrency), ct);
                return result.IsSuccess
                    ? Results.Created("tenant-modules", new { id = result.Value })
                    : TenantModulesErrors.ToProblem(result.Error!);
            })
            .WithName("RequestTenantModule")
            .WithSummary("Ask the platform for a paid module, accepting its price")
            .WithDescription("Send the price the user was shown. The platform must authorise the request "
                + "before the module is active. 409 MODULE_PRICE_CHANGED when the price moved since it was "
                + "shown, MODULE_UNAVAILABLE, MODULE_REQUEST_PENDING, MODULE_ALREADY_ACTIVE; 503 "
                + "TENANT_REGISTRY_UNAVAILABLE. Audited. Requires tenant:modules:request.")
            .RequireAuthorization(Permissions.CanRequestTenantModule.Code)
            .Produces(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("requests/{subscriptionId:guid}/cancel", async (Guid subscriptionId, ISender sender,
                    CancellationToken ct) =>
            {
                var result = await sender.Send(new CancelTenantModuleRequestCommand(subscriptionId), ct);
                return result.IsSuccess ? Results.NoContent() : TenantModulesErrors.ToProblem(result.Error!);
            })
            .WithName("CancelTenantModuleRequest")
            .WithSummary("Withdraw a module request the platform has not decided yet")
            .WithDescription("409 MODULE_REQUEST_NOT_PENDING once decided. Audited. Requires tenant:modules:request.")
            .RequireAuthorization(Permissions.CanRequestTenantModule.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{module}/deactivate", async (string module, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new DeactivateTenantModuleCommand(module), ct);
                return result.IsSuccess ? Results.NoContent() : TenantModulesErrors.ToProblem(result.Error!);
            })
            .WithName("DeactivateTenantModule")
            .WithSummary("Switch a module off, effective immediately")
            .WithDescription("No platform approval and no data deleted: the module's screens and routes close "
                + "on the next request, and switching it back on finds everything where it was. 409 "
                + "MODULE_NOT_ACTIVE. Audited. Requires tenant:modules:deactivate.")
            .RequireAuthorization(Permissions.CanDeactivateTenantModule.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }
}

public sealed record RequestTenantModuleBody(decimal AcceptedMonthlyPrice, string AcceptedCurrency);

// ── List ────────────────────────────────────────────────────────────────────────────────────────

internal sealed record ListTenantModulesQuery : IRequest<Result<TenantModulesDto>>;

internal sealed class ListTenantModulesHandler(ITenantModuleRegistry registry, ICurrentUser currentUser)
    : IRequestHandler<ListTenantModulesQuery, Result<TenantModulesDto>>
{
    public async Task<Result<TenantModulesDto>> Handle(ListTenantModulesQuery request, CancellationToken ct)
    {
        var answer = await registry.ListAsync(currentUser.TenantId, ct);
        return answer.IsSuccess
            ? Result.Ok(new TenantModulesDto(answer.Value!.Modules.Select(m => m.ToDto()).ToList()))
            : Result.Fail<TenantModulesDto>(answer.Error!);
    }
}

// ── Request ─────────────────────────────────────────────────────────────────────────────────────

/// <param name="AcceptedMonthlyPrice">The price the user was shown; the registry refuses any other.</param>
public sealed record RequestTenantModuleCommand(string Module, decimal AcceptedMonthlyPrice, string AcceptedCurrency)
    : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "TenantModule";
    public string? ResourceId => Module;
}

internal sealed class RequestTenantModuleValidator : AbstractValidator<RequestTenantModuleCommand>
{
    public RequestTenantModuleValidator()
    {
        RuleFor(x => x.AcceptedMonthlyPrice).GreaterThanOrEqualTo(0)
            .WithMessage("Le prix accepté ne peut pas être négatif.");
        RuleFor(x => x.AcceptedCurrency).NotEmpty().Length(3)
            .WithMessage("La devise est un code ISO à trois lettres, par exemple XOF.");
    }
}

internal sealed class RequestTenantModuleHandler(
    ITenantModuleRegistry registry,
    ICurrentUser currentUser,
    AdministrationDbContext db,
    ITenantCacheInvalidator tenantCache,
    TenantModuleNotifier notifier)
    : IRequestHandler<RequestTenantModuleCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(RequestTenantModuleCommand cmd, CancellationToken ct)
    {
        if (!TenantModules.IsKnown(cmd.Module))
            return Result.Fail<Guid>(TenantModulesErrors.ModuleUnknown);

        var email = await CurrentEmail.ReadAsync(db, currentUser, ct);
        if (email is null) return Result.Fail<Guid>(TenantModulesErrors.UserNotFound);

        var answer = await registry.RequestAsync(currentUser.TenantId, cmd.Module, currentUser.Id, email,
            cmd.AcceptedMonthlyPrice, cmd.AcceptedCurrency, ct);
        if (!answer.IsSuccess) return Result.Fail<Guid>(answer.Error!);

        await tenantCache.InvalidateAsync(currentUser.TenantId, ct);

        // The platform team decides: tell it there is something to decide.
        await notifier.RequestedAsync(currentUser.TenantId, answer.Value, cmd.Module, email,
            cmd.AcceptedMonthlyPrice, cmd.AcceptedCurrency, ct);
        return Result.Ok(answer.Value);
    }
}

// ── Cancel ──────────────────────────────────────────────────────────────────────────────────────

public sealed record CancelTenantModuleRequestCommand(Guid SubscriptionId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TenantModule";
    public string? ResourceId => SubscriptionId.ToString();
}

internal sealed class CancelTenantModuleRequestHandler(ITenantModuleRegistry registry, ICurrentUser currentUser)
    : IRequestHandler<CancelTenantModuleRequestCommand, Result>
{
    public async Task<Result> Handle(CancelTenantModuleRequestCommand cmd, CancellationToken ct)
    {
        var answer = await registry.CancelAsync(currentUser.TenantId, cmd.SubscriptionId, currentUser.Id, ct);
        return answer.IsSuccess ? Result.Ok() : Result.Fail(answer.Error!);
    }
}

// ── Deactivate ──────────────────────────────────────────────────────────────────────────────────

public sealed record DeactivateTenantModuleCommand(string Module)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TenantModule";
    public string? ResourceId => Module;
}

internal sealed class DeactivateTenantModuleHandler(
    ITenantModuleRegistry registry,
    ICurrentUser currentUser,
    AdministrationDbContext db,
    ITenantCacheInvalidator tenantCache,
    TenantModuleNotifier notifier,
    TimeProvider clock)
    : IRequestHandler<DeactivateTenantModuleCommand, Result>
{
    public async Task<Result> Handle(DeactivateTenantModuleCommand cmd, CancellationToken ct)
    {
        if (!TenantModules.IsKnown(cmd.Module))
            return Result.Fail(TenantModulesErrors.ModuleUnknown);

        var email = await CurrentEmail.ReadAsync(db, currentUser, ct);
        if (email is null) return Result.Fail(TenantModulesErrors.UserNotFound);

        var answer = await registry.DeactivateAsync(currentUser.TenantId, cmd.Module, currentUser.Id, email, ct);
        if (!answer.IsSuccess) return Result.Fail(answer.Error!);

        // Immediate means the next request: without this, the cached TenantInfo would keep the
        // module on for up to TenantStore:CacheTtl.
        await tenantCache.InvalidateAsync(currentUser.TenantId, ct);

        // No approval needed, but the invoice changes: the platform is told.
        await notifier.DeactivatedAsync(currentUser.TenantId, cmd.Module, email, clock.GetUtcNow(), ct);
        return Result.Ok();
    }
}

internal static class CurrentEmail
{
    /// <summary>The acting user's address, recorded by the registry next to the act.</summary>
    public static Task<string?> ReadAsync(AdministrationDbContext db, ICurrentUser currentUser, CancellationToken ct) =>
        db.Users.AsNoTracking()
            .Where(u => u.Id == currentUser.Id)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(ct);
}

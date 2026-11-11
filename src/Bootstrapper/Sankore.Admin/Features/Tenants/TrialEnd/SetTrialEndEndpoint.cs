using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;
using Sankore.Admin.Infrastructure.Auth;

namespace Sankore.Admin.Features.Tenants.TrialEnd;

/// <summary>
/// Prolongs (or shortens) a free trial from the institution's page (<c>docs/essai-gratuit-socle.md</c>,
/// lot 3). A platform decision: <c>RequirePlatformAdmin</c>, the service key is not enough. The new end
/// must be in the future — ending a trial today is lot 5's switch, not a date typed in the past.
/// </summary>
public static class SetTrialEndEndpoint
{
    public const string NotATrial = "TENANT_NOT_A_TRIAL";
    public const string EndInPast = "TRIAL_END_IN_PAST";

    public sealed record Request(DateTimeOffset TrialExpiresAt);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPut("/{id:guid}/trial-end", async (Guid id, Request body, ISender sender, CancellationToken ct) =>
                await sender.Send(new SetTrialEndCommand(id, body.TrialExpiresAt), ct) switch
                {
                    null => Results.NoContent(),
                    "NOT_FOUND" => Results.NotFound(),
                    var error => Results.Conflict(new { error }),
                })
            .RequirePlatformAdmin()
            .WithName("SetTenantTrialEnd")
            .WithSummary("Move the end of a free trial")
            .WithDescription("409 TENANT_NOT_A_TRIAL for a paying institution, TRIAL_END_IN_PAST for a date already past.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();
    }
}

/// <returns>Null on success, else <c>NOT_FOUND</c> or an error code.</returns>
public sealed record SetTrialEndCommand(Guid TenantId, DateTimeOffset TrialExpiresAt) : IRequest<string?>;

internal sealed class SetTrialEndHandler(AdminDbContext db, TimeProvider clock, ILogger<SetTrialEndHandler> logger)
    : IRequestHandler<SetTrialEndCommand, string?>
{
    public async Task<string?> Handle(SetTrialEndCommand request, CancellationToken ct)
    {
        var tenant = await db.Tenants.AsTracking().FirstOrDefaultAsync(t => t.Id == request.TenantId, ct);
        if (tenant is null) return "NOT_FOUND";
        if (tenant.Offer != TenantOffer.Trial) return SetTrialEndEndpoint.NotATrial;
        if (request.TrialExpiresAt <= clock.GetUtcNow()) return SetTrialEndEndpoint.EndInPast;

        var before = tenant.TrialExpiresAt;
        tenant.SetTrialEnd(request.TrialExpiresAt.ToUniversalTime());
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Trial of tenant {TenantId} ({TenantName}) moved from {Before} to {After}",
            tenant.Id, tenant.Name, before, tenant.TrialExpiresAt);
        return null;
    }
}

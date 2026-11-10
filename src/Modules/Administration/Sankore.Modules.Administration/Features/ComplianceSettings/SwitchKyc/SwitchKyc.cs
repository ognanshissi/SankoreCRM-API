namespace Sankore.Modules.Administration.Features.ComplianceSettings.SwitchKyc;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// Switches the institution's KYC on or off (docs/kyc-par-institution.md, lot 1). An
/// <see cref="ICommand"/>, so the audit records who did it, when, and which way.
///
/// <para>
/// Off is refused while a regulated module is active (<see cref="TenantKycErrors.RequiredByModule"/>),
/// a pending module request is not a reason. The row is the TEAM tenant's: the route is closed in
/// « Mode test » and the twin reads its production's switch.
/// </para>
/// </summary>
/// <param name="RestoredByModule">
/// Set only by <see cref="KycModuleRestorer"/>: the module whose activation put KYC back on.
/// </param>
public sealed record SwitchKycCommand(bool Enabled, string? RestoredByModule = null) : ICommand, IRequest<Result<SwitchKycResult>>;

/// <param name="Changed">False when the switch already said so: nothing was announced.</param>
public sealed record SwitchKycResult(bool Enabled, bool Changed);

public sealed record SwitchKycRequest(bool Enabled);

internal sealed class SwitchKycHandler(
    AdministrationDbContext db,
    ICurrentUser currentUser,
    ITenantKyc tenantKyc,
    TenantKycReader reader,
    TimeProvider clock,
    [FromKeyedServices(nameof(AdministrationDbContext))] IEventPublisher publisher)
    : IRequestHandler<SwitchKycCommand, Result<SwitchKycResult>>
{
    public async Task<Result<SwitchKycResult>> Handle(SwitchKycCommand cmd, CancellationToken ct)
    {
        var team = currentUser.TeamTenantId;

        if (!cmd.Enabled && (await tenantKyc.GetAsync(team, ct)).RequiredByModule is not null)
            return Result.Fail<SwitchKycResult>(TenantKycErrors.RequiredByModule);

        var now = clock.GetUtcNow();
        var settings = await db.ComplianceSettings
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(s => s.TenantId == team, ct);

        if (settings is null)
        {
            // Materialised on first write, like the rest of the screen: no row reads as ON.
            if (cmd.Enabled) return Result.Ok(new SwitchKycResult(true, false));
            settings = Domain.ComplianceSettings.CreateDefault(team, currentUser.Id, now);
            db.ComplianceSettings.Add(settings);
        }

        if (!settings.SwitchKyc(cmd.Enabled, currentUser.Id, now))
            return Result.Ok(new SwitchKycResult(cmd.Enabled, false));

        await publisher.PublishAsync(
            new TenantKycSwitchedEvent(team, cmd.Enabled, currentUser.Id, cmd.RestoredByModule), ct);
        await db.SaveChangesAsync(ct);
        await reader.InvalidateAsync(team, ct);

        return Result.Ok(new SwitchKycResult(cmd.Enabled, true));
    }
}

internal static class SwitchKycEndpoint
{
    public static IEndpointRouteBuilder MapSwitchKyc(this IEndpointRouteBuilder app)
    {
        app.MapPut("kyc", Handle)
            .WithName("SwitchKyc")
            .WithSummary("Switch the institution's KYC on or off (off only without a Banking - Microfinance or Insurance module)")
            .RequireAuthorization(Permissions.CanManageCompliance.Code)
            .LiveModeWritesOnly()
            .Accepts<SwitchKycRequest>("application/json")
            .Produces<SwitchKycResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(ISender sender, SwitchKycRequest body, CancellationToken ct)
    {
        var result = await sender.Send(new SwitchKycCommand(body.Enabled), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Conflict(new
            {
                error = result.Error,
                detail = "Le KYC reste obligatoire tant que le module « Banking - Microfinance » ou « Assurance » est actif.",
            });
    }
}

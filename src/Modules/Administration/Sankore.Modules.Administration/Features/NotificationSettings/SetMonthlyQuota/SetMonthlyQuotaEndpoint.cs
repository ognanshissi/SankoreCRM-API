using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.NotificationSettings.SetMonthlyQuota;

internal static class SetMonthlyQuotaEndpoint
{
    public static IEndpointRouteBuilder MapSetMonthlyQuota(this IEndpointRouteBuilder app)
    {
        app.MapPatch("quota", Handle)
            .WithName("SetMonthlyEmailQuota")
            .WithSummary("Set or clear the monthly email quota for the tenant (platform admin only)")
            .RequireAuthorization(Permissions.CanManageEmailQuota.Code)
            .Accepts<SetMonthlyQuotaCommand>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        SetMonthlyQuotaCommand command,
        CancellationToken ct)
    {
        var result = await sender.Send(command, ct);

        return result.IsSuccess
            ? Results.NoContent()
            : Results.BadRequest(new { error = result.Error });
    }
}

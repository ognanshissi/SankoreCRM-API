namespace Sankore.Modules.Administration.Features.SmsSettings.SetSmsMonthlyQuota;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class SetSmsMonthlyQuotaEndpoint
{
    public static IEndpointRouteBuilder MapSetSmsMonthlyQuota(this IEndpointRouteBuilder app)
    {
        app.MapPut("quota", Handle)
            .WithName("SetSmsMonthlyQuota")
            .WithSummary("Set or clear the tenant's monthly SMS allowance")
            .WithDescription(
                "Null clears the ceiling, which on a per-message channel is a decision with a "
                + "bill attached. Zero is refused: it would stop every message including a "
                + "customer's one-time code, and an operator who means that disables the channel "
                + "instead. Counted separately from the email allowance — a flood on one channel "
                + "must not silence the other. Requires notification:quota:manage.")
            .RequireAuthorization(Permissions.CanManageEmailQuota.Code)
            .Accepts<SetSmsMonthlyQuotaCommand>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender, SetSmsMonthlyQuotaCommand command, CancellationToken ct)
    {
        var result = await sender.Send(command, ct);

        return result.IsSuccess
            ? Results.NoContent()
            : Results.BadRequest(new { error = result.Error });
    }
}

namespace Sankore.Modules.Administration.Features.ComplianceSettings.UpdateComplianceSettings;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class UpdateComplianceSettingsEndpoint
{
    public static IEndpointRouteBuilder MapUpdateComplianceSettings(this IEndpointRouteBuilder app)
    {
        // PUT and not PATCH: the payload is the complete configuration — see the command for why
        // a partial update of the FATCA triple is not offered.
        app.MapPut("", Handle)
            .WithName("UpdateComplianceSettings")
            .WithSummary("Configure the FATCA regime and the PEP post-mandate window")
            .RequireAuthorization(Permissions.CanManageCompliance.Code)
            .Accepts<UpdateComplianceSettingsCommand>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender, UpdateComplianceSettingsCommand command, CancellationToken ct)
    {
        var result = await sender.Send(command, ct);

        return result.IsSuccess
            ? Results.NoContent()
            : Results.BadRequest(new { error = result.Error });
    }
}

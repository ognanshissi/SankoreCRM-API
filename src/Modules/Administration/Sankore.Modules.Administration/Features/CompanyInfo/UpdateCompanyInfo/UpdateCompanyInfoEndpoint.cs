using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.CompanyInfo.UpdateCompanyInfo;

internal static class UpdateCompanyInfoEndpoint
{
    public static IEndpointRouteBuilder MapUpdateCompanyInfo(this IEndpointRouteBuilder app)
    {
        app.MapPut("", Handle)
            .WithName("UpdateCompanyInfo")
            .WithSummary("Update branding and configuration for the current tenant")
            .RequireAuthorization(Permissions.CanUpdateCompanyInfo.Code)
            .Accepts<UpdateCompanyInfoCommand>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        UpdateCompanyInfoCommand command, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(command, ct);

        return result.IsSuccess
            ? Results.NoContent()
            : Results.BadRequest(new { error = result.Error });
    }
}

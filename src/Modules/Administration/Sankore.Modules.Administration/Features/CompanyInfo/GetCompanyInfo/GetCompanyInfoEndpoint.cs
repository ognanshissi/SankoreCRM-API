using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.CompanyInfo.GetCompanyInfo;

internal static class GetCompanyInfoEndpoint
{
    public static IEndpointRouteBuilder MapGetCompanyInfo(this IEndpointRouteBuilder app)
    {
        app.MapGet("", Handle)
            .WithName("GetCompanyInfo")
            .WithSummary("Get branding and configuration for the current tenant")
            .RequireAuthorization(Permissions.CanReadCompanyInfo.Code)
            .Produces<CompanyInfoDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender, ITenantContext tenantContext, CancellationToken ct)
    {
        var result = await sender.Send(new GetCompanyInfoQuery(tenantContext.CurrentTenantId), ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
}

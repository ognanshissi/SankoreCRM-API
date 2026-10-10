namespace Sankore.Modules.Administration.Features.ComplianceSettings.GetComplianceSettings;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class GetComplianceSettingsEndpoint
{
    public static IEndpointRouteBuilder MapGetComplianceSettings(this IEndpointRouteBuilder app)
    {
        app.MapGet("", Handle)
            .WithName("GetComplianceSettings")
            .WithSummary("Get the FATCA and PEP compliance parameters of the current tenant")
            .RequireAuthorization(Permissions.CanReadCompliance.Code)
            .Produces<ComplianceSettingsResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetComplianceSettingsQuery(), ct);
        return Results.Ok(result.Value);
    }
}

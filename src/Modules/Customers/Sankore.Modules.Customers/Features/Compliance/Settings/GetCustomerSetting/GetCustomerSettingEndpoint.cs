namespace Sankore.Modules.Customers.Features.Compliance.Settings.GetCustomerSetting;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.Compliance.Shared;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class GetCustomerSettingEndpoint
{
    public static IEndpointRouteBuilder MapGetCustomerSetting(this IEndpointRouteBuilder app)
    {
        app.MapGet("{key}", Handle)
            .WithName("GetCustomerSetting")
            .WithSummary("Read one Customers module setting")
            .WithDescription(
                "Returns a single M01 tenant setting. An unknown key answers 404 SETTING_UNKNOWN. " +
                "Requires permission: customers:read.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<CustomerSettingDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(string key, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetCustomerSettingQuery(key), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : ComplianceHttp.ToProblem(result.Error);
    }
}

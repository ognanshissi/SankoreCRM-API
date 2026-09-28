namespace Sankore.Modules.Customers.Features.Compliance.Settings.UpdateCustomerSetting;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.Compliance.Shared;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class UpdateCustomerSettingEndpoint
{
    public static IEndpointRouteBuilder MapUpdateCustomerSetting(this IEndpointRouteBuilder app)
    {
        app.MapPut("{key}", Handle)
            .WithName("UpdateCustomerSetting")
            .WithSummary("Update one Customers module setting")
            .WithDescription(
                "Writes a single M01 tenant setting. The value is validated against the type " +
                "declared for the key (int / bool / decimal / json / string); 'retention-years' " +
                "additionally cannot go below the 10-year regulatory floor. An unknown key " +
                "answers 404 SETTING_UNKNOWN. Audited. " +
                "Requires permission: customers:update_sensitive.")
            .RequireAuthorization(Permissions.CanUpdateCustomerSensitive.Code)
            .Produces<CustomerSettingDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        string key,
        UpdateCustomerSettingRequest req,
        ISender sender,
        CancellationToken ct)
    {
        // The key comes from the route, never from the body: a mismatch between the two would
        // otherwise let a caller update a key other than the one they are authorized against.
        var result = await sender.Send(new UpdateCustomerSettingCommand(key, req.Value), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : ComplianceHttp.ToProblem(result.Error);
    }
}

public sealed record UpdateCustomerSettingRequest(string Value);

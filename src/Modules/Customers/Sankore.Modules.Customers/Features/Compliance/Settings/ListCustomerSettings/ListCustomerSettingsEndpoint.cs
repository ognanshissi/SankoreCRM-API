namespace Sankore.Modules.Customers.Features.Compliance.Settings.ListCustomerSettings;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.Compliance.Shared;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class ListCustomerSettingsEndpoint
{
    public static IEndpointRouteBuilder MapListCustomerSettings(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("ListCustomerSettings")
            .WithSummary("List the Customers module settings of the current tenant")
            .WithDescription(
                "Returns every M01 tenant setting with its key, current value, declared type, " +
                "description, factory default and an IsDefault flag. A key the tenant has never " +
                "customized is returned with its factory value. Requires permission: customers:read.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<IReadOnlyList<CustomerSettingDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ListCustomerSettingsQuery(), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : ComplianceHttp.ToProblem(result.Error);
    }
}

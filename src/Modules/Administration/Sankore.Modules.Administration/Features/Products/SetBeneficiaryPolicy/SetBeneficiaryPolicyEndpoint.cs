namespace Sankore.Modules.Administration.Features.Products.SetBeneficiaryPolicy;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class SetBeneficiaryPolicyEndpoint
{
    public static IEndpointRouteBuilder MapSetBeneficiaryPolicy(this IEndpointRouteBuilder app)
    {
        app.MapPut("{productId:guid}/beneficiary-policy", Handle)
            .WithName("SetProductBeneficiaryPolicy")
            .WithSummary("Configure the household, designation roles and required fields of a product line")
            .RequireAuthorization(Permissions.CanUpdateProduct.Code)
            .Accepts<SetBeneficiaryPolicyRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid productId, SetBeneficiaryPolicyRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new SetBeneficiaryPolicyCommand(
            ProductId:                      productId,
            HouseholdEnabled:               req.HouseholdEnabled,
            AllowMultipleHoldings:          req.AllowMultipleHoldings,
            AllowedRoles:                   req.AllowedRoles,
            MaxChildren:                    req.MaxChildren,
            ChildMaxAge:                    req.ChildMaxAge,
            RequiredFieldsBeforeConversion: req.RequiredFieldsBeforeConversion,
            RequiredKycTier:                req.RequiredKycTier,
            IntegrationSystem:              req.IntegrationSystem), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error switch
            {
                "PRODUCT_NOT_FOUND" => Results.NotFound(new { error = result.Error }),
                _ => Results.BadRequest(new { error = result.Error })
            };
    }
}

/// <summary>
/// The complete policy of a product line. Every field is replaced — see the command for why there
/// is no PATCH.
/// </summary>
public sealed record SetBeneficiaryPolicyRequest(
    bool HouseholdEnabled,
    bool AllowMultipleHoldings = true,
    IReadOnlyList<DesignationRoleInput>? AllowedRoles = null,
    int? MaxChildren = null,
    int? ChildMaxAge = null,
    IReadOnlyList<string>? RequiredFieldsBeforeConversion = null,
    string? RequiredKycTier = null,
    string? IntegrationSystem = null);

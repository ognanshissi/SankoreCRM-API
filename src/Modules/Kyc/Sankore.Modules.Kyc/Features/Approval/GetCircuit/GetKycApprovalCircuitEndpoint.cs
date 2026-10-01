namespace Sankore.Modules.Kyc.Features.Approval.GetCircuit;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class GetKycApprovalCircuitEndpoint
{
    internal static IEndpointRouteBuilder MapGetKycApprovalCircuit(this IEndpointRouteBuilder app)
    {
        app.MapGet("{kycFileId:guid}/approval", Handle)
            .WithName("GetKycApprovalCircuit")
            .WithSummary("Read the approval circuit of a KYC file")
            .WithDescription(
                "Before validation the levels are a preview computed from the file's vigilance; "
                + "once the circuit is open they are the persisted steps with their decisions.")
            // kyc:read, not kyc:approve: everyone who may open a file may see who has to sign it.
            .RequireAuthorization(Permissions.CanReadKycFile.Code)
            .Produces<KycApprovalCircuitDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(Guid kycFileId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetKycApprovalCircuitQuery(kycFileId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            // 404 and never 403, same rule as the rest of the module: the existence of a file of
            // another tenant must not leak.
            : Results.NotFound(new { error = result.Error });
    }
}

namespace Sankore.Modules.Kyc.Features.Routing.Controls.ListControls;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class ListKycAutoApprovalControlsEndpoint
{
    internal static IEndpointRouteBuilder MapListKycAutoApprovalControls(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("auto-approval-controls", Handle)
            .WithName("ListKycAutoApprovalControls")
            .WithSummary("The sampled automatic KYC approvals awaiting a compliance control")
            .WithDescription(
                "Server-side paging, oldest first — a compliance queue is a backlog and the row "
                + "that has waited longest is the most overdue. Filterable on status (Pending, "
                + "Compliant, NonCompliant); pendingCount counts the whole queue rather than the "
                + "page. Carries no score, no flag and no field read on a document: the officer "
                + "opens the file for those. NOT narrowed by the agency perimeter, unlike the other "
                + "KYC lists — the control is about the institution's whole exposure, and the "
                + "permission is what bounds it. Requires kyc:auto-approval:control.")
            .RequireAuthorization(Permissions.CanControlKycAutoApproval.Code)
            .Produces<KycAutoApprovalControlPage>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        [FromQuery] string? status,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new ListKycAutoApprovalControlsQuery(status, page ?? 1, pageSize ?? 25), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { error = result.Error });
    }
}

namespace Sankore.Modules.Customers.Features.Compliance.Retention.ListRetentionCandidates;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.Compliance.Shared;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class ListRetentionCandidatesEndpoint
{
    public static IEndpointRouteBuilder MapListRetentionCandidates(this IEndpointRouteBuilder app)
    {
        app.MapGet("retention/candidates", Handle)
            .WithName("ListClientRetentionCandidates")
            .WithSummary("List the archived clients that may now be anonymized")
            .WithDescription(
                "Paginated list of the anonymization proposals produced by the monthly retention " +
                "scan: clients archived longer ago than the tenant's 'retention-years' setting " +
                "(floor 10 years) and whose KYC file the KYC module reports as cleared. A client " +
                "the KYC module has not cleared is never proposed. Restricted to the caller's " +
                "agency perimeter. No identity field is returned. " +
                "Requires permission: customers:archive.")
            .RequireAuthorization(Permissions.CanArchiveCustomer.Code)
            .Produces<PagedResult<RetentionCandidateDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        CancellationToken ct,
        int page = 1,
        int pageSize = 20)
    {
        var result = await sender.Send(new ListRetentionCandidatesQuery(page, pageSize), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : ComplianceHttp.ToProblem(result.Error);
    }
}

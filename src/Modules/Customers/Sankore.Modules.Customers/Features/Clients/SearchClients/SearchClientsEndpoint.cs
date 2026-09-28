namespace Sankore.Modules.Customers.Features.Clients.SearchClients;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class SearchClientsEndpoint
{
    public static IEndpointRouteBuilder MapSearchClients(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("SearchClients")
            .WithSummary("Search clients")
            .WithDescription(
                "Paginated, multi-criteria search. All filters are cumulative (AND). "
                + "phone and identityDocumentNumber are matched by EQUALITY ON THE BLIND INDEX after "
                + "normalization, so '+225 07 08 09 18' and '07080918' find the same client and no "
                + "encrypted column is ever decrypted to filter. "
                + "name is a prefix search on the normalized SearchKey column (upper-cased, "
                + "accent-free, surname first): 'TRA' finds 'Traoré', and a given name is matched too. "
                + "A name shorter than 3 characters is ignored. "
                + "Results are ordered by displayName. Rows are restricted to the caller's agency "
                + "perimeter (own agency + descendants + explicit attributions); a tenant-wide account "
                + "sees every agency. "
                + "Performance: the ux_clients_number, ux_clients_identity_doc and (TenantId, SearchKey) "
                + "indexes plus the (TenantId, Status/AgencyId/AdvisorUserId/SegmentCode) indexes keep "
                + "this query within the p95 < 300 ms objective at 100 000 clients per tenant — the "
                + "count and the page are two server-side queries, nothing is materialised before "
                + "Skip/Take. "
                + "primaryPhoneMasked is masked; use POST clients/{clientId}/reveal for a clear value. "
                + "Requires permission: customers:read.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<PagedResult<ClientSearchItemDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        string? clientNumber,
        string? phone,
        string? identityDocumentNumber,
        string? name,
        ClientStatus? status,
        Guid? agencyId,
        Guid? advisorUserId,
        ClientType? type,
        string? segmentCode,
        int? page,
        int? pageSize,
        CancellationToken ct)
    {
        var result = await sender.Send(new SearchClientsQuery(
            ClientNumber: clientNumber,
            Phone: phone,
            IdentityDocumentNumber: identityDocumentNumber,
            Name: name,
            Status: status,
            AgencyId: agencyId,
            AdvisorUserId: advisorUserId,
            Type: type,
            SegmentCode: segmentCode,
            Page: page ?? 1,
            PageSize: pageSize ?? 20), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

namespace Sankore.Modules.Customers.Features.Compliance.Export.RequestClientExport;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.SearchClients;
using Sankore.Modules.Customers.Features.Compliance.Shared;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class RequestClientExportEndpoint
{
    public static IEndpointRouteBuilder MapRequestClientExport(this IEndpointRouteBuilder app)
    {
        app.MapPost("exports", Handle)
            .WithName("RequestClientExport")
            .WithSummary("Queue a CSV export of a client search")
            .WithDescription(
                "Replays the given search filters in the background and produces a CSV with the "
                + "columns client_number, display_name, client_type, status, agency_id, "
                + "advisor_user_id, kyc_status, risk_level, segment_code, primary_phone_masked, "
                + "identity_document_masked, created_at. The phone and identity-document columns "
                + "are masked — a CSV is never a reveal surface. The export is generated with the "
                + "REQUESTER's agency perimeter, not the background account's, and is capped at "
                + "50 000 rows. Audited, filters included. Returns 202 with the export id; poll "
                + "GET clients/exports/{exportId} for the download link. "
                + "Requires permission: customers:export.")
            .RequireAuthorization(Permissions.CanExportCustomers.Code)
            .Produces<RequestClientExportResult>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        RequestClientExportRequest req,
        ISender sender,
        CancellationToken ct)
    {
        // Page/PageSize of the search are deliberately not exposed: an export is not paginated,
        // it is capped. Passing 1 / int.MaxValue would only invite confusion.
        var filters = new SearchClientsQuery(
            ClientNumber: req.ClientNumber,
            Phone: req.Phone,
            IdentityDocumentNumber: req.IdentityDocumentNumber,
            Name: req.Name,
            Status: req.Status,
            AgencyId: req.AgencyId,
            AdvisorUserId: req.AdvisorUserId,
            Type: req.Type,
            SegmentCode: req.SegmentCode);

        var result = await sender.Send(new RequestClientExportCommand(filters), ct);

        return result.IsSuccess
            ? Results.Accepted($"/api/v1/clients/exports/{result.Value.ExportId}", result.Value)
            : ComplianceHttp.ToProblem(result.Error);
    }
}

/// <summary>
/// Export filters — the same criteria as <c>GET clients</c>, minus the pagination.
/// </summary>
public sealed record RequestClientExportRequest(
    string? ClientNumber,
    string? Phone,
    string? IdentityDocumentNumber,
    string? Name,
    ClientStatus? Status,
    Guid? AgencyId,
    Guid? AdvisorUserId,
    ClientType? Type,
    string? SegmentCode);

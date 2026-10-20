namespace Sankore.Modules.Leads.Features.ListLeads;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.GetLead;
using Sankore.Shared.Kernel;

public static class ListLeadsEndpoint
{
    public static IEndpointRouteBuilder MapListLeads(this IEndpointRouteBuilder app)
    {
        app.MapGet("", Handle)
            .WithName("ListLeads")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<PagedResult<LeadDto>>()
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        CancellationToken ct,
        int page = 1,
        int pageSize = 20,
        LeadStatus? status = null,
        PipelineStage? pipelineStage = null,
        LeadSource? source = null,
        Guid? ownerId = null,
        Guid? agencyId = null,
        string? search = null,
        string? tag = null,
        LeadIntentLevel? intentLevel = null,
        Guid? leadSourceConfigId = null,
        bool? awaitingDispatch = null,
        // Repeated: `cf=segment:Commercant&cf=vip:oui`. Custom-field filters, all of which must hold.
        [Microsoft.AspNetCore.Mvc.FromQuery(Name = "cf")] string[]? customFilters = null)
    {
        // Appended last, like the query record's own member: this call is positional.
        var result = await sender.Send(
            new ListLeadsQuery(page, pageSize, status, pipelineStage, source, ownerId, agencyId, search, tag, intentLevel, leadSourceConfigId, awaitingDispatch,
                customFilters), ct);

        // A refused filter answers 400 rather than an unfiltered page under a filtered heading.
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(title: "Invalid filter", detail: result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

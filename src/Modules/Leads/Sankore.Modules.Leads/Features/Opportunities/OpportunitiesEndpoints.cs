namespace Sankore.Modules.Leads.Features.Opportunities;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Opportunities.CreateOpportunity;
using Sankore.Modules.Leads.Features.Opportunities.CreateOpportunityForCustomer;
using Sankore.Modules.Leads.Features.Opportunities.GetOpportunity;
using Sankore.Modules.Leads.Features.Opportunities.ListOpportunities;
using Sankore.Shared.Kernel;

public static class OpportunitiesEndpoints
{
    public static IEndpointRouteBuilder MapOpportunitiesEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("opportunities");

        // GET opportunities
        group.MapGet("", ListOpportunities)
            .WithName("ListOpportunities")
            .WithTags("Opportunities")
            .RequireAuthorization(Permissions.CanReadOpportunities.Code)
            .Produces<IReadOnlyList<OpportunityDto>>()
            .WithOpenApi();

        // GET opportunities/{id}
        group.MapGet("{id:guid}", GetOpportunity)
            .WithName("GetOpportunity")
            .WithTags("Opportunities")
            .RequireAuthorization(Permissions.CanReadOpportunities.Code)
            .Produces<OpportunityDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST opportunities (from lead)
        group.MapPost("", CreateFromLead)
            .WithName("CreateOpportunity")
            .WithTags("Opportunities")
            .RequireAuthorization(Permissions.CanManageOpportunities.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST opportunities/for-customer (from existing customer)
        group.MapPost("for-customer", CreateForCustomer)
            .WithName("CreateOpportunityForCustomer")
            .WithTags("Opportunities")
            .RequireAuthorization(Permissions.CanManageOpportunities.Code)
            .Produces<CreateOpportunityForCustomerResult>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> ListOpportunities(
        ISender sender, CancellationToken ct,
        Guid? leadId = null, Guid? customerId = null, OpportunityStage? stage = null)
    {
        var result = await sender.Send(
            new ListOpportunitiesQuery(leadId, customerId, stage), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> GetOpportunity(
        Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetOpportunityQuery(id), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> CreateFromLead(
        CreateOpportunityRequest req,
        ISender sender,
        ITenantContext tenant,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreateOpportunityCommand(
            TenantId:          tenant.CurrentTenantId,
            LeadId:            req.LeadId,
            Title:             req.Title,
            Product:           req.Product,
            OwnerId:           req.OwnerId,
            Description:       req.Description,
            EstimatedAmount:   req.EstimatedAmount,
            EstimatedCurrency: req.EstimatedCurrency,
            ExpectedCloseDate: req.ExpectedCloseDate), ct);

        return result.IsSuccess
            ? Results.Created($"opportunities/{result.Value}", result.Value)
            : Results.Problem(title: "Create failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> CreateForCustomer(
        CreateOpportunityForCustomerRequest req,
        ISender sender,
        ITenantContext tenant,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreateOpportunityForCustomerCommand(
            TenantId:          tenant.CurrentTenantId,
            CustomerEntityId:  req.CustomerEntityId,
            Title:             req.Title,
            Product:           req.Product,
            OwnerId:           req.OwnerId,
            LeadId:            req.LeadId,
            Description:       req.Description,
            EstimatedAmount:   req.EstimatedAmount,
            EstimatedCurrency: req.EstimatedCurrency,
            ExpectedCloseDate: req.ExpectedCloseDate,
            CustomerPhone:     req.CustomerPhone,
            Force:             req.Force), ct);

        if (!result.IsSuccess)
            return Results.Problem(title: "Create failed", detail: result.Error, statusCode: 422);

        if (result.Value!.DuplicateDetected)
            return Results.Ok(result.Value);

        return Results.Created($"opportunities/{result.Value.OpportunityId}", result.Value);
    }
}

public sealed record CreateOpportunityRequest(
    Guid LeadId,
    string Title,
    string Product,
    Guid? OwnerId = null,
    string? Description = null,
    decimal? EstimatedAmount = null,
    string? EstimatedCurrency = null,
    DateTimeOffset? ExpectedCloseDate = null);

public sealed record CreateOpportunityForCustomerRequest(
    Guid CustomerEntityId,
    string Title,
    string Product,
    Guid? OwnerId = null,
    Guid? LeadId = null,
    string? Description = null,
    decimal? EstimatedAmount = null,
    string? EstimatedCurrency = null,
    DateTimeOffset? ExpectedCloseDate = null,
    string? CustomerPhone = null,
    bool Force = false);

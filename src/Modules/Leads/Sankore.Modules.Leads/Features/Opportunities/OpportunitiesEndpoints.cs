namespace Sankore.Modules.Leads.Features.Opportunities;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Opportunities.AdvanceStage;
using Sankore.Modules.Leads.Features.Opportunities.CloseOpportunity;
using Sankore.Modules.Leads.Features.Opportunities.CreateOpportunity;
using Sankore.Modules.Leads.Features.Opportunities.CreateOpportunityForCustomer;
using Sankore.Modules.Leads.Features.Opportunities.GetOpportunity;
using Sankore.Modules.Leads.Features.Opportunities.ListOpportunities;
using Sankore.Modules.Leads.Features.Opportunities.MoveToStep;
using Sankore.Modules.Leads.Features.Opportunities.UpdateOpportunity;
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

        // PUT opportunities/{id}/stage
        group.MapPut("{id:guid}/stage", AdvanceStage)
            .WithName("AdvanceOpportunityStage")
            .WithTags("Opportunities")
            .RequireAuthorization(Permissions.CanManageOpportunities.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // PUT opportunities/{id}/step
        group.MapPut("{id:guid}/step", MoveToStep)
            .WithName("MoveOpportunityToStep")
            .WithTags("Opportunities")
            .RequireAuthorization(Permissions.CanManageOpportunities.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST opportunities/{id}/close
        group.MapPost("{id:guid}/close", Close)
            .WithName("CloseOpportunity")
            .WithTags("Opportunities")
            .RequireAuthorization(Permissions.CanManageOpportunities.Code)
            .Produces<CloseOpportunityResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // PUT opportunities/{id}
        group.MapPut("{id:guid}", Update)
            .WithName("UpdateOpportunity")
            .WithTags("Opportunities")
            .RequireAuthorization(Permissions.CanManageOpportunities.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();

        return app;
    }

    /// <summary>
    /// Moving the cursor along the pipeline. <b>Closing is refused here</b> and sent to its own
    /// endpoint: a closing carries a motive and opens a contract, which is not a thing a caller
    /// should be able to do by picking a value in a dropdown.
    /// </summary>
    private static async Task<IResult> AdvanceStage(
        Guid id,
        AdvanceOpportunityStageRequest req,
        ISender sender,
        CancellationToken ct)
    {
        if (req.Stage is OpportunityStage.ClosedWon or OpportunityStage.ClosedLost)
        {
            return Results.Problem(
                title: "Stage update failed",
                detail: OpportunityErrors.CloseThroughCloseEndpoint,
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var result = await sender.Send(
            new AdvanceOpportunityStageCommand(id, req.Stage, req.ExpectedUpdatedAt), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error switch
            {
                OpportunityErrors.NotFound => Results.NotFound(new { error = result.Error }),
                "CONFLICT"                 => Results.Conflict(new { error = result.Error }),
                _ => Results.Problem(
                        title: "Stage update failed", detail: result.Error, statusCode: 422),
            };
    }

    private static async Task<IResult> MoveToStep(
        Guid id,
        MoveOpportunityToStepRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new MoveOpportunityToStepCommand(id, req.StepId, req.ExpectedUpdatedAt), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error switch
            {
                OpportunityErrors.NotFound => Results.NotFound(new { error = result.Error }),
                "CONFLICT"                 => Results.Conflict(new { error = result.Error }),
                _ => Results.Problem(
                        title: "Step update failed", detail: result.Error, statusCode: 422),
            };
    }

    /// <summary>
    /// 200 and not 204, because the body carries the draft contract the closing opened — or the
    /// null that tells the screen to offer the subscription by hand.
    /// </summary>
    private static async Task<IResult> Close(
        Guid id,
        CloseOpportunityRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new CloseOpportunityCommand(id, req.Outcome, req.Reason, req.ExpectedUpdatedAt), ct);

        return result.IsSuccess
            ? Results.Ok(new CloseOpportunityResponse(result.Value!.HoldingId))
            : result.Error switch
            {
                OpportunityErrors.NotFound => Results.NotFound(new { error = result.Error }),
                "CONFLICT"                 => Results.Conflict(new { error = result.Error }),
                _ => Results.Problem(
                        title: "Close failed", detail: result.Error, statusCode: 422),
            };
    }

    private static async Task<IResult> Update(
        Guid id,
        UpdateOpportunityRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new UpdateOpportunityCommand(
                OpportunityId:     id,
                Title:             req.Title,
                Description:       req.Description,
                EstimatedAmount:   req.EstimatedAmount,
                EstimatedCurrency: req.EstimatedCurrency,
                ExpectedCloseDate: req.ExpectedCloseDate,
                OwnerId:           req.OwnerId,
                ExpectedUpdatedAt: req.ExpectedUpdatedAt,
                Product:           req.Product,
                NextStep:          req.NextStep,
                NextStepDueOn:     req.NextStepDueOn,
                ConfirmSeparateNeed: req.ConfirmSeparateNeed), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error switch
            {
                OpportunityErrors.NotFound => Results.NotFound(new { error = result.Error }),
                "CONFLICT"                 => Results.Conflict(new { error = result.Error }),
                _ => Results.Problem(
                        title: "Update failed", detail: result.Error, statusCode: 422),
            };
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

/// <param name="Product">Facultatif : vide tant que le besoin n'est rattaché à aucun produit.</param>
public sealed record CreateOpportunityRequest(
    Guid LeadId,
    string Title,
    string? Product,
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

/// <param name="ExpectedUpdatedAt">
/// Ce que l'appelant a lu en dernier. Null saute le contrôle — l'onglet des demandes l'envoie, un
/// script d'intégration n'a pas à le connaître.
/// </param>
public sealed record AdvanceOpportunityStageRequest(
    OpportunityStage Stage,
    DateTimeOffset? ExpectedUpdatedAt = null);

/// <param name="Outcome">`ClosedWon` ou `ClosedLost`. Toute autre valeur est refusée.</param>
/// <param name="Reason">Le motif, écrit tel quel sur la demande.</param>
public sealed record CloseOpportunityRequest(
    OpportunityStage Outcome,
    string? Reason = null,
    DateTimeOffset? ExpectedUpdatedAt = null);

/// <param name="HoldingId">
/// La souscription en brouillon que la clôture a ouverte, quand elle en a ouvert une. Null est une
/// réponse ordinaire : demande perdue, demande sans fiche client, ou produit que le catalogue ne
/// reconnaît pas.
/// </param>
public sealed record CloseOpportunityResponse(Guid? HoldingId);

/// <summary>Tous les champs sont facultatifs : null laisse en place.</summary>
public sealed record UpdateOpportunityRequest(
    string? Title = null,
    string? Description = null,
    decimal? EstimatedAmount = null,
    string? EstimatedCurrency = null,
    DateTimeOffset? ExpectedCloseDate = null,
    Guid? OwnerId = null,
    DateTimeOffset? ExpectedUpdatedAt = null,
    /// <summary>Le produit, libellé du catalogue ; vide le remet « à préciser ».</summary>
    string? Product = null,
    /// <summary>La prochaine action ; vide l'efface, avec son échéance.</summary>
    string? NextStep = null,
    DateOnly? NextStepDueOn = null,
    /// <summary>Vrai : l'agent confirme que cette demande est un autre besoin que celui du lead.</summary>
    bool ConfirmSeparateNeed = false);

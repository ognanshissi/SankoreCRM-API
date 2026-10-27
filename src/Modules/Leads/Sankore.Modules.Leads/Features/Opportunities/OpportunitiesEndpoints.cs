namespace Sankore.Modules.Leads.Features.Opportunities;

using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Sankore.Modules.Leads.Infrastructure.TaskAttachments;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Opportunities.AdvanceStage;
using Sankore.Modules.Leads.Features.Opportunities.CloseOpportunity;
using Sankore.Modules.Leads.Features.Opportunities.CreateOpportunity;
using Sankore.Modules.Leads.Features.Opportunities.CreateOpportunityForCustomer;
using Sankore.Modules.Leads.Features.Opportunities.GetOpportunity;
using Sankore.Modules.Leads.Features.Opportunities.ListOpportunities;
using Sankore.Modules.Leads.Features.Opportunities.MoveToStep;
using Sankore.Modules.Leads.Features.Opportunities.UpdateOpportunity;
using Sankore.Modules.Leads.Features.Questionnaires;
using Sankore.Shared.Kernel;

public static class OpportunitiesEndpoints
{
    public static IEndpointRouteBuilder MapOpportunitiesEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("opportunities");

        group.MapProjectQuestionnaireEndpoints();

        // GET opportunities
        group.MapGet("", ListOpportunities)
            .WithName("ListOpportunities")
            .WithTags("Opportunities")
            .RequireAuthorization(Permissions.CanReadOpportunities.Code)
            .Produces<IReadOnlyList<OpportunityDto>>()
            .WithOpenApi();

        // GET opportunities/template?product= — what a NEW project on this product will follow
        group.MapGet("template", GetTemplateForProduct)
            .WithName("GetProjectTemplateForProduct")
            .WithTags("Opportunities")
            .RequireAuthorization(Permissions.CanReadOpportunities.Code)
            .Produces<ProjectTemplateSummaryDto>()
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

        // PUT opportunities/{id}/checklist/{itemId} — ticks or unticks a point of the current template
        group.MapPut("{id:guid}/checklist/{itemId:guid}", SetChecklistItem)
            .WithName("SetProjectChecklistItem")
            .WithTags("Opportunities")
            .RequireAuthorization(Permissions.CanManageOpportunities.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // GET opportunities/{id}/validations — the circuits its steps started, and their decisions
        group.MapGet("{id:guid}/validations", GetValidations)
            .WithName("GetProjectValidations")
            .WithTags("Opportunities")
            .RequireAuthorization(Permissions.CanReadOpportunities.Code)
            .Produces<IReadOnlyList<StepConditions.ProjectValidationDto>>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST opportunities/{id}/workflow/restart — after a refusal, runs the current step's circuit again
        group.MapPost("{id:guid}/workflow/restart", RestartWorkflow)
            .WithName("RestartProjectWorkflow")
            .WithTags("Opportunities")
            .RequireAuthorization(Permissions.CanManageOpportunities.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
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

        // POST opportunities/{id}/holding — opens (or finds) the draft contract of a won project
        group.MapPost("{id:guid}/holding", OpenHolding)
            .WithName("OpenProjectHolding")
            .WithTags("Opportunities")
            .RequireAuthorization(Permissions.CanManageOpportunities.Code)
            .Produces<CloseOpportunityResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST opportunities/{id}/template-version — follow the latest version of its template
        group.MapPost("{id:guid}/template-version", async (Guid id, ISender sender, CancellationToken ct) =>
                FieldResult(await sender.Send(new TemplateVersion.FollowLatestTemplateVersionCommand(id), ct)))
            .WithName("FollowLatestProjectTemplateVersion")
            .WithTags("Opportunities")
            .RequireAuthorization(Permissions.CanManageOpportunities.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // PUT opportunities/{id}/fields — the values of the template's fields (merge: null clears)
        group.MapPut("{id:guid}/fields", async (Guid id, SetProjectFieldsRequest req, ISender sender, CancellationToken ct) =>
                FieldResult(await sender.Send(new ProjectFields.SetProjectFieldsCommand(id, req.Values ?? new()), ct)))
            .WithName("SetProjectFields")
            .WithTags("Opportunities")
            .RequireAuthorization(Permissions.CanManageOpportunities.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST opportunities/{id}/attachments — a file for an Attachment field of the template
        group.MapPost("{id:guid}/attachments", async (
                Guid id, [FromForm] string fieldKey, IFormFile file, IOptions<TaskAttachmentStorageOptions> options,
                ISender sender, CancellationToken ct) =>
            {
                // Refused before reading: the size is known from the request, the bytes are not needed.
                if (file.Length > options.Value.MaxBytes)
                    return Results.UnprocessableEntity(new { error = "TASK_ATTACHMENT_TOO_LARGE" });

                using var buffer = new MemoryStream((int)file.Length);
                await file.CopyToAsync(buffer, ct);
                var result = await sender.Send(
                    new ProjectFields.StoreProjectAttachment(id, fieldKey, file.FileName, buffer.ToArray()), ct);
                return result.IsSuccess ? Results.Ok(result.Value) : FieldResult(Result.Fail(result.Error!));
            })
            .WithName("AddProjectAttachment")
            .WithTags("Opportunities")
            .WithSummary("Attaches a file (PDF, image, Office document, text; 10 MB) to an Attachment field: stored encrypted")
            .RequireAuthorization(Permissions.CanManageOpportunities.Code)
            .Accepts<IFormFile>("multipart/form-data")
            .DisableAntiforgery()
            .Produces<ProjectFields.ProjectAttachmentDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        group.MapGet("{id:guid}/attachments/{attachmentId:guid}/content", async (
                Guid id, Guid attachmentId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new ProjectFields.GetProjectAttachmentContentQuery(id, attachmentId), ct);
                return result.IsSuccess
                    ? Results.File(result.Value!.Content, result.Value.ContentType, result.Value.FileName)
                    : Results.NotFound();
            })
            .WithName("DownloadProjectAttachment")
            .WithTags("Opportunities")
            .RequireAuthorization(Permissions.CanReadOpportunities.Code)
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        group.MapDelete("{id:guid}/attachments/{attachmentId:guid}", async (
                Guid id, Guid attachmentId, ISender sender, CancellationToken ct) =>
                FieldResult(await sender.Send(new ProjectFields.RemoveProjectAttachmentCommand(id, attachmentId), ct)))
            .WithName("RemoveProjectAttachment")
            .WithTags("Opportunities")
            .RequireAuthorization(Permissions.CanManageOpportunities.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
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

    /// <summary>
    /// The won project's contract, in the closing's response shape. The error travels under
    /// <c>error</c>: M01's codes (HOLDING_ALREADY_HELD…) are the agent's answer, not a fault.
    /// </summary>
    private static async Task<IResult> OpenHolding(Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new OpenHolding.OpenProjectHoldingCommand(id), ct);

        return result.IsSuccess
            ? Results.Ok(new CloseOpportunityResponse(result.Value))
            : result.Error == OpportunityErrors.NotFound
                ? Results.NotFound(new { error = result.Error })
                : Results.UnprocessableEntity(new { error = result.Error });
    }

    /// <summary>The field and attachment routes: their codes under <c>error</c>, like the holdings'.</summary>
    private static IResult FieldResult(Result result) => result.IsSuccess
        ? Results.NoContent()
        : result.Error is OpportunityErrors.NotFound or ProjectFields.ProjectAttachmentHandlers.NotFound
            ? Results.NotFound(new { error = result.Error })
            : Results.UnprocessableEntity(new { error = result.Error });

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

    /// <summary>
    /// The template a project on <paramref name="product"/> would follow, as the creation forms need
    /// it before the project exists: how to name the amount, whether the close date is required.
    /// Agents may read it — <c>project-templates</c> is for those who manage the steps.
    /// </summary>
    private static async Task<IResult> GetTemplateForProduct(
        string? product, ProjectTemplates.ProjectTemplateResolver templates, CancellationToken ct)
    {
        var template = await templates.ResolveAsync(product, ct);
        return Results.Ok(new ProjectTemplateSummaryDto(
            template.Name, template.TracksForecast, template.AmountKind, template.CriticalCloseDate));
    }

    private static async Task<IResult> SetChecklistItem(
        Guid id, Guid itemId, StepConditions.SetProjectChecklistItemRequest req, ISender sender, CancellationToken ct)
        => StepConditionResult(await sender.Send(new StepConditions.SetProjectChecklistItemCommand(id, itemId, req.Done), ct));

    private static async Task<IResult> GetValidations(Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new StepConditions.GetProjectValidationsQuery(id), ct);
        return result.IsSuccess ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> RestartWorkflow(Guid id, ISender sender, CancellationToken ct)
        => StepConditionResult(await sender.Send(new StepConditions.RestartProjectWorkflowCommand(id), ct));

    private static IResult StepConditionResult(Result result) => result.IsSuccess
        ? Results.NoContent()
        : result.Error == OpportunityErrors.NotFound
            ? Results.NotFound(new { error = result.Error })
            : Results.Problem(title: "Project step condition", detail: result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);

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

/// <param name="Values">Field key → value, as the template's field type expects; null clears a value.</param>
public sealed record SetProjectFieldsRequest(Dictionary<string, JsonElement>? Values);

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

/// <param name="TracksForecast">The project will carry a probability and an expected close date.</param>
/// <param name="AmountKind">What its amount stands for: Generic, Loan, Premium or Deposit.</param>
/// <param name="CriticalCloseDate">The close date is a deadline: creating the project without one is refused.</param>
public sealed record ProjectTemplateSummaryDto(
    string Name, bool TracksForecast, ProjectAmountKind AmountKind, bool CriticalCloseDate);

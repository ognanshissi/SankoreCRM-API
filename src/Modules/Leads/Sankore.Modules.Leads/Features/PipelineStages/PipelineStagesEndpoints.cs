namespace Sankore.Modules.Leads.Features.PipelineStages;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Features.PipelineStages.ActivatePipelineStage;
using Sankore.Modules.Leads.Features.PipelineStages.CreatePipelineStage;
using Sankore.Modules.Leads.Features.PipelineStages.DeactivatePipelineStage;
using Sankore.Modules.Leads.Features.PipelineStages.ListPipelineStages;
using Sankore.Modules.Leads.Features.PipelineStages.UpdatePipelineStage;
using Sankore.Shared.Kernel;

public static class PipelineStagesEndpoints
{
    public static IEndpointRouteBuilder MapPipelineStagesEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("pipeline-stages");

        // GET pipeline-stages
        group.MapGet("", ListStages)
            .WithName("ListPipelineStages")
            .WithTags("Pipeline Stages")
            .RequireAuthorization(Permissions.CanReadPipelineStages.Code)
            .Produces<IReadOnlyList<PipelineStageConfigDto>>()
            .WithOpenApi();

        // POST pipeline-stages
        group.MapPost("", CreateStage)
            .WithName("CreatePipelineStage")
            .WithTags("Pipeline Stages")
            .RequireAuthorization(Permissions.CanManagePipelineStages.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // PUT pipeline-stages/{id}
        group.MapPut("{stageId:guid}", UpdateStage)
            .WithName("UpdatePipelineStageConfig")
            .WithTags("Pipeline Stages")
            .RequireAuthorization(Permissions.CanManagePipelineStages.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST pipeline-stages/{id}/activate
        group.MapPost("{stageId:guid}/activate", ActivateStage)
            .WithName("ActivatePipelineStage")
            .WithTags("Pipeline Stages")
            .RequireAuthorization(Permissions.CanManagePipelineStages.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST pipeline-stages/{id}/deactivate
        group.MapPost("{stageId:guid}/deactivate", DeactivateStage)
            .WithName("DeactivatePipelineStage")
            .WithTags("Pipeline Stages")
            .RequireAuthorization(Permissions.CanManagePipelineStages.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> ListStages(
        ISender sender, CancellationToken ct, bool? activeOnly = null)
    {
        var result = await sender.Send(new ListPipelineStagesQuery(activeOnly), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> CreateStage(
        CreatePipelineStageRequest req,
        ISender sender,
        ITenantContext tenant,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreatePipelineStageCommand(
            TenantId:     tenant.CurrentTenantId,
            Code:         req.Code,
            Label:        req.Label,
            DisplayOrder: req.DisplayOrder,
            Description:  req.Description,
            Color:        req.Color,
            IsFinal:      req.IsFinal), ct);

        return result.IsSuccess
            ? Results.Created($"leads/pipeline-stages/{result.Value}", result.Value)
            : Results.Problem(title: "Create failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> UpdateStage(
        Guid stageId,
        UpdatePipelineStageConfigRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new UpdatePipelineStageConfigCommand(
            StageId:      stageId,
            Label:        req.Label,
            DisplayOrder: req.DisplayOrder,
            Description:  req.Description,
            Color:        req.Color,
            IsFinal:      req.IsFinal), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "PIPELINE_STAGE_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Update failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> ActivateStage(
        Guid stageId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ActivatePipelineStageCommand(stageId), ct);
        return result.IsSuccess ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> DeactivateStage(
        Guid stageId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new DeactivatePipelineStageCommand(stageId), ct);

        if (result.IsSuccess)
            return Results.NoContent();

        return result.Error == "PIPELINE_STAGE_NOT_FOUND"
            ? Results.NotFound()
            : Results.Problem(title: "Deactivate failed", detail: result.Error, statusCode: 422);
    }
}

public sealed record CreatePipelineStageRequest(
    string Code,
    string Label,
    int DisplayOrder,
    string? Description = null,
    string? Color = null,
    bool IsFinal = false);

public sealed record UpdatePipelineStageConfigRequest(
    string Label,
    int DisplayOrder,
    string? Description = null,
    string? Color = null,
    bool IsFinal = false);

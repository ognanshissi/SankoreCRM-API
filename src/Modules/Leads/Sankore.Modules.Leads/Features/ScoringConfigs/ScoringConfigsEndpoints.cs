namespace Sankore.Modules.Leads.Features.ScoringConfigs;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Features.ScoringConfigs.ActivateScoringConfig;
using Sankore.Modules.Leads.Features.ScoringConfigs.CreateScoringConfig;
using Sankore.Modules.Leads.Features.ScoringConfigs.GetScoringConfig;
using Sankore.Modules.Leads.Features.ScoringConfigs.ListScoringConfigs;
using Sankore.Modules.Leads.Features.ScoringConfigs.UpdateScoringConfig;
using Sankore.Shared.Kernel;

public static class ScoringConfigsEndpoints
{
    public static IEndpointRouteBuilder MapScoringConfigsEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("scoring-configs");

        // GET scoring-configs
        group.MapGet("", ListConfigs)
            .WithName("ListScoringConfigs")
            .WithTags("Scoring Configs")
            .RequireAuthorization(Permissions.CanReadScoringConfigs.Code)
            .Produces<IReadOnlyList<ScoringConfigDto>>()
            .WithOpenApi();

        // GET scoring-configs/{id}
        group.MapGet("{configId:guid}", GetConfig)
            .WithName("GetScoringConfig")
            .WithTags("Scoring Configs")
            .RequireAuthorization(Permissions.CanReadScoringConfigs.Code)
            .Produces<ScoringConfigDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST scoring-configs
        group.MapPost("", CreateConfig)
            .WithName("CreateScoringConfig")
            .WithTags("Scoring Configs")
            .RequireAuthorization(Permissions.CanManageScoringConfigs.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // PUT scoring-configs/{id}
        group.MapPut("{configId:guid}", UpdateConfig)
            .WithName("UpdateScoringConfig")
            .WithTags("Scoring Configs")
            .RequireAuthorization(Permissions.CanManageScoringConfigs.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST scoring-configs/{id}/activate
        group.MapPost("{configId:guid}/activate", ActivateConfig)
            .WithName("ActivateScoringConfig")
            .WithTags("Scoring Configs")
            .RequireAuthorization(Permissions.CanManageScoringConfigs.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> ListConfigs(
        ISender sender, CancellationToken ct, bool? activeOnly = null)
    {
        var result = await sender.Send(new ListScoringConfigsQuery(activeOnly), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> GetConfig(
        Guid configId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetScoringConfigQuery(configId), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> CreateConfig(
        CreateScoringConfigRequest req,
        ISender sender,
        ITenantContext tenant,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreateScoringConfigCommand(
            TenantId:               tenant.CurrentTenantId,
            Name:                   req.Name,
            QualificationThreshold: req.QualificationThreshold,
            WeightDemographics:     req.WeightDemographics,
            WeightEngagement:       req.WeightEngagement,
            WeightProduct:          req.WeightProduct,
            WeightChannel:          req.WeightChannel,
            WeightRecency:          req.WeightRecency), ct);

        return result.IsSuccess
            ? Results.Created($"leads/scoring-configs/{result.Value}", result.Value)
            : Results.Problem(title: "Create failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> UpdateConfig(
        Guid configId,
        UpdateScoringConfigRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new UpdateScoringConfigCommand(
            ConfigId:               configId,
            Name:                   req.Name,
            QualificationThreshold: req.QualificationThreshold,
            WeightDemographics:     req.WeightDemographics,
            WeightEngagement:       req.WeightEngagement,
            WeightProduct:          req.WeightProduct,
            WeightChannel:          req.WeightChannel,
            WeightRecency:          req.WeightRecency), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "SCORING_CONFIG_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Update failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> ActivateConfig(
        Guid configId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ActivateScoringConfigCommand(configId), ct);
        return result.IsSuccess ? Results.NoContent() : Results.NotFound();
    }
}

public sealed record CreateScoringConfigRequest(
    string Name,
    int QualificationThreshold = 60,
    double WeightDemographics = 0,
    double WeightEngagement = 0,
    double WeightProduct = 0,
    double WeightChannel = 0,
    double WeightRecency = 0);

public sealed record UpdateScoringConfigRequest(
    string Name,
    int QualificationThreshold,
    double WeightDemographics,
    double WeightEngagement,
    double WeightProduct,
    double WeightChannel,
    double WeightRecency);

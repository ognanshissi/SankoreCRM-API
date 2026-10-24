namespace Sankore.Modules.Leads.Features.ProjectTemplates;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// The project step templates. Guarded by the pipeline-stage permissions: the settings screen
/// they replace held them, and every role that could configure the pipeline can configure this.
/// Agents never need this route — each project carries its own steps.
/// </summary>
public static class ProjectTemplatesEndpoints
{
    public static IEndpointRouteBuilder MapProjectTemplatesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("project-templates").WithTags("Project Templates");

        group.MapGet("", List)
            .WithName("ListProjectTemplates")
            .RequireAuthorization(Permissions.CanReadPipelineStages.Code)
            .Produces<IReadOnlyList<ProjectTemplateDto>>()
            .WithOpenApi();

        group.MapPost("", Create)
            .WithName("CreateProjectTemplate")
            .RequireAuthorization(Permissions.CanManagePipelineStages.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        group.MapPut("{templateId:guid}", Update)
            .WithName("UpdateProjectTemplate")
            .RequireAuthorization(Permissions.CanManagePipelineStages.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        group.MapDelete("{templateId:guid}", Delete)
            .WithName("DeleteProjectTemplate")
            .RequireAuthorization(Permissions.CanManagePipelineStages.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> List(ISender sender, CancellationToken ct)
        => Results.Ok((await sender.Send(new ListProjectTemplatesQuery(), ct)).Value);

    private static async Task<IResult> Create(
        CreateProjectTemplateRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new CreateProjectTemplateCommand(
            req.Name, req.Category, req.ProductCode, Inputs(req.Steps),
            new ProjectForecastOptions(req.TracksForecast, req.AmountKind, req.CriticalCloseDate)), ct);

        return result.IsSuccess
            ? Results.Created($"project-templates/{result.Value}", result.Value)
            : ToProblem(result.Error!);
    }

    private static async Task<IResult> Update(
        Guid templateId, UpdateProjectTemplateRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new UpdateProjectTemplateCommand(templateId, req.Name, Inputs(req.Steps),
                new ProjectForecastOptions(req.TracksForecast, req.AmountKind, req.CriticalCloseDate)), ct);

        return result.IsSuccess ? Results.NoContent() : ToProblem(result.Error!);
    }

    private static async Task<IResult> Delete(Guid templateId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new DeleteProjectTemplateCommand(templateId), ct);
        return result.IsSuccess ? Results.NoContent() : ToProblem(result.Error!);
    }

    private static IReadOnlyList<ProjectStepInput> Inputs(IReadOnlyList<ProjectStepRequest>? steps)
        => (steps ?? []).Select(s => new ProjectStepInput(
            s.Id, s.Label ?? string.Empty, s.Phase, s.Probability, s.Suggestion, s.OpensClientFile, s.IsApproval,
            s.RequiresQuestionnaires)).ToList();

    private static IResult ToProblem(string error) => error switch
    {
        ProjectTemplateErrors.NotFound   => Results.NotFound(new { error }),
        ProjectTemplateErrors.ScopeTaken => Results.Problem(title: "Template scope taken", detail: error, statusCode: 409),
        _ => Results.Problem(title: "Invalid project template", detail: error, statusCode: 422),
    };
}

/// <param name="Category">A family (<c>ProductCategory</c>). Leave both scopes empty for the default.</param>
/// <param name="ProductCode">A catalogue product's code, for a template of its own.</param>
public sealed record CreateProjectTemplateRequest(
    string Name,
    ProductCategory? Category,
    string? ProductCode,
    IReadOnlyList<ProjectStepRequest> Steps,
    /// <summary>The projects carry a probability and an expected close date (a sales forecast).</summary>
    bool TracksForecast = true,
    /// <summary>What a project's amount stands for: Generic, Loan, Premium or Deposit.</summary>
    ProjectAmountKind AmountKind = ProjectAmountKind.Generic,
    /// <summary>The close date is a deadline (requires TracksForecast).</summary>
    bool CriticalCloseDate = false);

public sealed record UpdateProjectTemplateRequest(
    string Name,
    IReadOnlyList<ProjectStepRequest> Steps,
    /// <summary>The projects carry a probability and an expected close date (a sales forecast).</summary>
    bool TracksForecast = true,
    /// <summary>What a project's amount stands for: Generic, Loan, Premium or Deposit.</summary>
    ProjectAmountKind AmountKind = ProjectAmountKind.Generic,
    /// <summary>The close date is a deadline (requires TracksForecast).</summary>
    bool CriticalCloseDate = false);

/// <param name="Id">Send back an existing step's id: the projects on it stay there.</param>
/// <param name="Phase">One of the five open phases, never going backwards along the list.</param>
/// <param name="Probability">Chance of signing, 1 to 99 %.</param>
/// <param name="OpensClientFile">Reaching this step needs a client record; at most one step.</param>
/// <param name="IsApproval">The decision step, gated on the client's KYC; at most one, never before the client file.</param>
/// <param name="RequiresQuestionnaires">Reaching this step needs the product's questionnaires answered; at most one step.</param>
public sealed record ProjectStepRequest(
    Guid? Id,
    string? Label,
    OpportunityStage Phase,
    int Probability,
    string? Suggestion,
    bool OpensClientFile = false,
    bool IsApproval = false,
    bool RequiresQuestionnaires = false);

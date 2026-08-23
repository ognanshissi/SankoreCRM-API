using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.CreateTemplate;

internal static class CreateTemplateEndpoint
{
    public static IEndpointRouteBuilder MapCreateTemplate(this IEndpointRouteBuilder app)
    {
        app.MapPost(string.Empty, Handle)
            .WithName("CreateWorkflowTemplate")
            .WithSummary("Create a new workflow template")
            .RequireAuthorization(Permissions.CanCreateWorkflow.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        CreateTemplateRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new CreateTemplateCommand(req.EntityType, req.Name, req.Description), ct);

        return result.IsSuccess
            ? Results.Created($"/api/v1/workflow/templates/{result.Value}", result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

public sealed record CreateTemplateRequest(
    string EntityType,
    string Name,
    string? Description);

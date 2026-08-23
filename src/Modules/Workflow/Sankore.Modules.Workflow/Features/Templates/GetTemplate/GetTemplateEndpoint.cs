using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.GetTemplate;

internal static class GetTemplateEndpoint
{
    public static IEndpointRouteBuilder MapGetTemplate(this IEndpointRouteBuilder app)
    {
        app.MapGet("{id:guid}", Handle)
            .WithName("GetWorkflowTemplate")
            .WithSummary("Get a workflow template by Id")
            .RequireAuthorization(Permissions.CanReadWorkflow.Code)
            .Produces<WorkflowTemplateDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetTemplateQuery(id), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(result.Error);
    }
}

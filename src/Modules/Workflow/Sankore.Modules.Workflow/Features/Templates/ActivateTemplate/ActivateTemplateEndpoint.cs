using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.ActivateTemplate;

internal static class ActivateTemplateEndpoint
{
    public static IEndpointRouteBuilder MapActivateTemplate(this IEndpointRouteBuilder app)
    {
        app.MapPost("{id:guid}/activate", Handle)
            .WithName("ActivateWorkflowTemplate")
            .WithSummary("Activate a workflow template (makes it usable for new instances)")
            .RequireAuthorization(Permissions.CanActivateWorkflow.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ActivateTemplateCommand(id), ct);
        return result.IsSuccess
            ? Results.NoContent()
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.DeactivateTemplate;

internal static class DeactivateTemplateEndpoint
{
    public static IEndpointRouteBuilder MapDeactivateTemplate(this IEndpointRouteBuilder app)
    {
        app.MapDelete("{id:guid}", Handle)
            .WithName("DeactivateWorkflowTemplate")
            .WithSummary("Deactivate a workflow template (soft delete)")
            .RequireAuthorization(Permissions.CanDeleteWorkflow.Code)
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
        var result = await sender.Send(new DeactivateTemplateCommand(id), ct);
        return result.IsSuccess
            ? Results.NoContent()
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

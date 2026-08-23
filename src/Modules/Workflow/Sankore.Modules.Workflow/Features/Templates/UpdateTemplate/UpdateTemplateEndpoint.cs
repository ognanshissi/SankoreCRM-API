using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.UpdateTemplate;

internal static class UpdateTemplateEndpoint
{
    public static IEndpointRouteBuilder MapUpdateTemplate(this IEndpointRouteBuilder app)
    {
        app.MapPut("{id:guid}", Handle)
            .WithName("UpdateWorkflowTemplate")
            .WithSummary("Update a workflow template name/description")
            .RequireAuthorization(Permissions.CanUpdateWorkflow.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid id, UpdateTemplateRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new UpdateTemplateCommand(id, req.Name, req.Description), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

public sealed record UpdateTemplateRequest(string Name, string? Description);

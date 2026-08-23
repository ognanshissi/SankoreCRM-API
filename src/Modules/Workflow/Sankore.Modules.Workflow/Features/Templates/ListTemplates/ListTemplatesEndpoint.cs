using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.ListTemplates;

internal static class ListTemplatesEndpoint
{
    public static IEndpointRouteBuilder MapListTemplates(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("ListWorkflowTemplates")
            .WithSummary("List workflow templates for the current tenant")
            .RequireAuthorization(Permissions.CanReadWorkflow.Code)
            .Produces<List<WorkflowTemplateDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        bool? activeOnly, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ListTemplatesQuery(activeOnly), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status500InternalServerError);
    }
}

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Rules.ListRules;

internal static class ListRulesEndpoint
{
    internal static IEndpointRouteBuilder MapListRules(this IEndpointRouteBuilder app)
    {
        app.MapGet("{templateId:guid}/steps/{stepId:guid}/rules",
            async (Guid templateId, Guid stepId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new ListRulesQuery(templateId, stepId), ct);
                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.NotFound(new { error = result.Error });
            })
        .WithName("ListWorkflowRules")
        .WithSummary("List all rules configured for a workflow step")
        .RequireAuthorization(Permissions.CanReadWorkflow.Code)
        .Produces<List<RuleDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .WithOpenApi();

        return app;
    }
}

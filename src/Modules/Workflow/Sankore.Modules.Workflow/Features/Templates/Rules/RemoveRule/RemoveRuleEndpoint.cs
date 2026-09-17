using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Rules.RemoveRule;

internal static class RemoveRuleEndpoint
{
    internal static IEndpointRouteBuilder MapRemoveRule(this IEndpointRouteBuilder app)
    {
        app.MapDelete("{templateId:guid}/steps/{stepId:guid}/rules/{ruleId:guid}",
            async (Guid templateId, Guid stepId, Guid ruleId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new RemoveRuleCommand(templateId, stepId, ruleId), ct);
                return result.IsSuccess
                    ? Results.NoContent()
                    : Results.BadRequest(new { error = result.Error });
            })
        .WithName("RemoveWorkflowRule")
        .WithSummary("Remove a rule from a workflow step")
        .RequireAuthorization(Permissions.CanManageWorkflowSteps.Code)
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .WithOpenApi();

        return app;
    }
}

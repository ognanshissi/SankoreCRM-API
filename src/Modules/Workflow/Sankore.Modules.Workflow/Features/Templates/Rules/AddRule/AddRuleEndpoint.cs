using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Workflow.Domain;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Rules.AddRule;

internal static class AddRuleEndpoint
{
    internal static IEndpointRouteBuilder MapAddRule(this IEndpointRouteBuilder app)
    {
        app.MapPost("{templateId:guid}/steps/{stepId:guid}/rules",
            async (Guid templateId, Guid stepId, AddRuleRequest body, ISender sender, CancellationToken ct) =>
            {
                var cmd = new AddRuleCommand(templateId, stepId,
                    body.RuleType, body.Field, body.Operator, body.Value, body.LogicalGroup);

                var result = await sender.Send(cmd, ct);
                return result.IsSuccess
                    ? Results.Ok(new { ruleId = result.Value })
                    : Results.BadRequest(new { error = result.Error });
            })
        .WithName("AddWorkflowRule")
        .WithSummary("Add a condition rule to a workflow step")
        .RequireAuthorization(Permissions.CanManageWorkflowSteps.Code)
        .Produces<object>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .WithOpenApi();

        return app;
    }
}

internal sealed record AddRuleRequest(
    RuleType RuleType,
    string Field,
    RuleOperator Operator,
    string Value,
    int LogicalGroup = 0);

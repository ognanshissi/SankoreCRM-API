using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Workflow.Domain;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Triggers.ListTriggers;

internal static class ListTriggersEndpoint
{
    internal static IEndpointRouteBuilder MapListTriggers(this IEndpointRouteBuilder app)
    {
        app.MapGet("{templateId:guid}/triggers", async (
            Guid templateId,
            ISender sender,
            CancellationToken ct) =>
        {
            var result = await sender.Send(new ListTriggersQuery(templateId), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.BadRequest(new { error = result.Error });
        })
        .WithName("ListTriggers")
        .WithSummary("List all event triggers registered on a workflow template.")
        .RequireAuthorization(Permissions.CanManageWorkflowTriggers.Code)
        .Produces<IReadOnlyList<TriggerDto>>()
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        return app;
    }
}

internal sealed record TriggerDto(
    Guid Id,
    TriggerType TriggerType,
    string EventName,
    string? ConditionJson,
    bool IsActive,
    DateTimeOffset CreatedAt);

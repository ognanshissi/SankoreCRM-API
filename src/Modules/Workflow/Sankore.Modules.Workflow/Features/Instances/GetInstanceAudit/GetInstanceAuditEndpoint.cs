using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.GetInstanceAudit;

internal static class GetInstanceAuditEndpoint
{
    internal static IEndpointRouteBuilder MapGetInstanceAudit(this IEndpointRouteBuilder app)
    {
        app.MapGet("{instanceId:guid}/audit",
            async (Guid instanceId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new GetInstanceAuditQuery(instanceId), ct);
                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.NotFound(new { error = result.Error });
            })
        .WithName("GetWorkflowInstanceAudit")
        .WithSummary("Get the full audit trail for a workflow instance")
        .RequireAuthorization()
        .Produces<List<WorkflowAuditEntryDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .WithOpenApi();

        return app;
    }
}

internal sealed record WorkflowAuditEntryDto(
    Guid Id,
    Guid? FromStateId,
    Guid? ToStateId,
    string EventCode,
    Guid ActedByUserId,
    string? Comment,
    DateTimeOffset OccurredAt);

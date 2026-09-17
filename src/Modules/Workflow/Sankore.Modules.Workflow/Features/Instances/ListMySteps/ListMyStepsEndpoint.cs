using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Sankore.Modules.Workflow.Features.Instances.ListMySteps;

internal static class ListMyStepsEndpoint
{
    internal static IEndpointRouteBuilder MapListMySteps(this IEndpointRouteBuilder app)
    {
        app.MapGet("my-steps", async (ISender sender, CancellationToken ct) =>
        {
            var result = await sender.Send(new ListMyStepsQuery(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.BadRequest(new { error = result.Error });
        })
        .WithName("ListMySteps")
        .WithSummary("Return all active workflow steps assigned to the current user or their role.")
        .RequireAuthorization()
        .Produces<IReadOnlyList<MyStepDto>>()
        .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }
}

internal sealed record MyStepDto(
    Guid StepId,
    Guid InstanceId,
    string StepName,
    string? ApproverRoleCode,
    Guid? AssignedToUserId,
    DateTimeOffset? DueAt,
    DateTimeOffset CreatedAt);

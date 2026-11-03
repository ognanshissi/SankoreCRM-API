using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Modules.Workflow.Infrastructure.Gates;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.GetInstanceRequirements;

/// <summary>
/// « Avant d'approuver » (product owner, 2026-11-02): what the open step of an instance waits for on
/// the record, item by item, and the record where a person completes it — a project's form is its
/// lead's. The same checks <c>ApproveStepHandler</c> refuses on, read before anyone clicks.
/// </summary>
public sealed record GetInstanceRequirementsQuery(Guid InstanceId) : IRequest<Result<InstanceRequirementsDto>>;

public sealed record InstanceRequirementsDto(
    Guid? InstanceStepId,
    string? StepName,
    IReadOnlyList<InstanceRequirementDto> Items,
    string? CompleteOnEntityType,
    Guid? CompleteOnEntityId);

public sealed record InstanceRequirementDto(string Label, bool Satisfied, string? FieldKey);

internal sealed class GetInstanceRequirementsHandler(WorkflowDbContext db, RecordStepGates gates)
    : IRequestHandler<GetInstanceRequirementsQuery, Result<InstanceRequirementsDto>>
{
    private static readonly InstanceRequirementsDto None = new(null, null, [], null, null);

    public async Task<Result<InstanceRequirementsDto>> Handle(GetInstanceRequirementsQuery request, CancellationToken ct)
    {
        var instance = await db.WorkflowInstances
            .Include(i => i.Steps)
            .FirstOrDefaultAsync(i => i.Id == request.InstanceId, ct);
        if (instance is null) return Result.Fail<InstanceRequirementsDto>($"Instance {request.InstanceId} not found.");

        var step = instance.Steps
            .Where(s => s.Status == StepStatus.AwaitingApproval)
            .OrderBy(s => s.Order)
            .FirstOrDefault();
        if (step is null) return Result.Ok(None);

        var requirements = await gates.RequirementsAsync(
            instance.TenantId, instance.EntityType, instance.EntityId, step.Kind, step.ConfigJson, ct);
        if (requirements is null) return Result.Ok(None with { InstanceStepId = step.Id, StepName = step.Name });

        return Result.Ok(new InstanceRequirementsDto(
            step.Id,
            step.Name,
            requirements.Items.Select(i => new InstanceRequirementDto(i.Label, i.Satisfied, i.Key)).ToList(),
            requirements.CompleteOn?.EntityType,
            requirements.CompleteOn?.Id));
    }
}

internal static class GetInstanceRequirementsEndpoint
{
    public static IEndpointRouteBuilder MapGetInstanceRequirements(this IEndpointRouteBuilder app)
    {
        app.MapGet("{id:guid}/requirements", async (Guid id, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new GetInstanceRequirementsQuery(id), ct);
                return result.IsSuccess ? Results.Ok(result.Value) : Results.NotFound(result.Error);
            })
            .WithName("GetWorkflowInstanceRequirements")
            .WithSummary("What the open step of an instance waits for on its record")
            .RequireAuthorization(Permissions.CanViewWorkflowInstances.Code)
            .Produces<InstanceRequirementsDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }
}

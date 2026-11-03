using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Modules.Workflow.Infrastructure.Notifications;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.RemindStep;

/// <summary>
/// « Relancer » from « Santé des workflows » (product owner, 2026-11-03): a bell for whoever must act
/// on the open step — the person it was handed to, else the holders of its role in the record's agency
/// or above, else the record's agent — and a line in the history. One reminder per person and hour:
/// a second click in the same hour tells nobody twice.
/// </summary>
public sealed record RemindStepCommand(Guid InstanceId, Guid StepId) : IRequest<Result<RemindStepResult>>, ICommand;

/// <param name="Reminded">How many people were told; 0 when nobody holds the step.</param>
public sealed record RemindStepResult(int Reminded);

internal sealed class RemindStepHandler(
    WorkflowDbContext db,
    ICurrentUser currentUser,
    RecordFactsReader facts,
    IAdministrationModule administration,
    INotificationsModule notifications,
    TimeProvider clock)
    : IRequestHandler<RemindStepCommand, Result<RemindStepResult>>
{
    public const string Category = "workflow.reminder";

    public async Task<Result<RemindStepResult>> Handle(RemindStepCommand request, CancellationToken ct)
    {
        var instance = await db.WorkflowInstances
            .Include(i => i.Steps)
            .FirstOrDefaultAsync(i => i.Id == request.InstanceId, ct);
        if (instance is null) return Result.Fail<RemindStepResult>("Workflow instance not found.");

        var step = instance.Steps.FirstOrDefault(s => s.Id == request.StepId);
        if (step is null || step.Status != StepStatus.AwaitingApproval || instance.Status != WorkflowStatus.InProgress)
            return Result.Fail<RemindStepResult>("Cette étape n'attend plus de décision.");

        var record = await facts.ReadAsync(instance.TenantId, instance.EntityType, instance.EntityId, ct);
        IReadOnlyCollection<Guid> people = step.AssignedToUserId is { } holder ? [holder]
            : step.ApproverRoleCode is { } role
                ? await administration.ListActiveUsersInRolesAsync(instance.TenantId, [role], record?.AgencyId, ct)
                : record?.AgentId is { } agent ? [agent] : [];
        people = people.Where(p => p != currentUser.Id).Distinct().ToList();
        if (people.Count == 0) return Result.Ok(new RemindStepResult(0));

        var now = clock.GetUtcNow();
        var late = step.DueAt is { } due && due < now
            ? $" Elle est en retard de {Math.Round((now - due).TotalHours)} h."
            : "";
        var who = record is null ? "" : $" ({string.Join(" ", new[] { record.FirstName, record.LastName }.Where(n => !string.IsNullOrWhiteSpace(n)))})";
        foreach (var person in people)
            await notifications.NotifyInAppAsync(new InAppNotificationRequest(
                instance.TenantId, person, Category,
                $"Relance : « {step.Name} »",
                $"Une décision est attendue de votre part sur cette étape{who}.{late}",
                $"/settings/workflows/instances/{instance.Id}",
                $"workflow.reminder:{step.Id}:{person}:{now:yyyyMMddHH}"), ct);

        var names = await administration.GetUserNamesAsync(instance.TenantId, people, ct);
        var listed = people.Count <= 3
            ? string.Join(", ", people.Select(p => names.GetValueOrDefault(p) ?? "un utilisateur"))
            : $"{people.Count} personnes";
        db.WorkflowAuditEntries.Add(WorkflowAuditEntry.Create(
            instance.TenantId, instance.Id, step.StepDefinitionId, step.StepDefinitionId,
            EventCodes.Reminded, currentUser.Id, $"Relance envoyée à {listed}.", instance.ContextJson));
        await db.SaveChangesAsync(ct);

        return Result.Ok(new RemindStepResult(people.Count));
    }
}

internal static class RemindStepEndpoint
{
    public static IEndpointRouteBuilder MapRemindStep(this IEndpointRouteBuilder app)
    {
        app.MapPost("{instanceId:guid}/steps/{stepId:guid}/remind", async (
                Guid instanceId, Guid stepId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new RemindStepCommand(instanceId, stepId), ct);
                return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
            })
            .WithName("RemindWorkflowStep")
            .WithSummary("Remind whoever must act on an open step")
            .RequireAuthorization(Permissions.CanAssignWorkflowStep.Code)
            .Produces<RemindStepResult>(StatusCodes.Status200OK)
            .Produces<object>(StatusCodes.Status400BadRequest);
        return app;
    }
}

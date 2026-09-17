using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.GetTemplateDiff;

internal sealed class GetTemplateDiffHandler(WorkflowDbContext db)
    : IRequestHandler<GetTemplateDiffQuery, Result<TemplateDiffDto>>
{
    public async Task<Result<TemplateDiffDto>> Handle(
        GetTemplateDiffQuery request, CancellationToken ct)
    {
        var from = await db.WorkflowTemplates
            .Include(t => t.Steps).ThenInclude(s => s.Rules)
            .Include(t => t.Transitions)
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct);

        if (from is null)
            return Result.Fail<TemplateDiffDto>("Source template not found.");

        var to = await db.WorkflowTemplates
            .Include(t => t.Steps).ThenInclude(s => s.Rules)
            .Include(t => t.Transitions)
            .FirstOrDefaultAsync(t => t.Id == request.CompareWithId, ct);

        if (to is null)
            return Result.Fail<TemplateDiffDto>("Target template not found.");

        if (from.EntityType != to.EntityType)
            return Result.Fail<TemplateDiffDto>(
                "Templates belong to different entity types and cannot be compared.");

        return Result.Ok(new TemplateDiffDto(
            FromTemplateId:  from.Id,
            FromVersion:     from.Version,
            ToTemplateId:    to.Id,
            ToVersion:       to.Version,
            EntityType:      from.EntityType,
            Steps:           DiffSteps(from, to),
            Transitions:     DiffTransitions(from, to)));
    }

    // ── Steps ──────────────────────────────────────────────────────────────

    private static StepsDiffDto DiffSteps(WorkflowTemplate from, WorkflowTemplate to)
    {
        var fromByOrder = from.Steps.ToDictionary(s => s.Order);
        var toByOrder   = to.Steps.ToDictionary(s => s.Order);

        var allOrders = fromByOrder.Keys.Union(toByOrder.Keys).OrderBy(o => o).ToList();

        var added   = new List<StepSnapshotDto>();
        var removed = new List<StepSnapshotDto>();
        var changed = new List<StepChangedDto>();

        foreach (var order in allOrders)
        {
            var hasFrom = fromByOrder.TryGetValue(order, out var f);
            var hasTo   = toByOrder.TryGetValue(order, out var t);

            if (!hasFrom)              { added.Add(Snapshot(t!));   continue; }
            if (!hasTo)                { removed.Add(Snapshot(f!)); continue; }

            var fieldChanges = DetectStepChanges(f!, t!);
            if (fieldChanges.Count > 0)
                changed.Add(new StepChangedDto(order, f!.Code, fieldChanges));
        }

        return new StepsDiffDto(added, removed, changed);
    }

    private static Dictionary<string, FieldChangedDto> DetectStepChanges(
        WorkflowStepDefinition f, WorkflowStepDefinition t)
    {
        var changes = new Dictionary<string, FieldChangedDto>();
        Check(changes, "name",            f.Name,             t.Name);
        Check(changes, "description",     f.Description,      t.Description);
        Check(changes, "approverRoleCode",f.ApproverRoleCode, t.ApproverRoleCode);
        Check(changes, "timeoutHours",    f.TimeoutHours?.ToString(), t.TimeoutHours?.ToString());
        return changes;
    }

    private static void Check(
        Dictionary<string, FieldChangedDto> acc, string key, string? from, string? to)
    {
        if (from != to) acc[key] = new FieldChangedDto(from, to);
    }

    private static StepSnapshotDto Snapshot(WorkflowStepDefinition s) =>
        new(s.Order, s.Code, s.Name, s.Description, s.ApproverRoleCode, s.TimeoutHours);

    // ── Transitions ────────────────────────────────────────────────────────

    private static TransitionsDiffDto DiffTransitions(WorkflowTemplate from, WorkflowTemplate to)
    {
        // Build step-order lookup so we can identify transitions by human-readable keys.
        var fromOrderById = from.Steps.ToDictionary(s => s.Id, s => s.Order);
        var toOrderById   = to.Steps.ToDictionary(s => s.Id, s => s.Order);

        var fromKeys = from.Transitions.ToDictionary(t => TransitionKey(t, fromOrderById));
        var toKeys   = to.Transitions.ToDictionary(t => TransitionKey(t, toOrderById));

        var added   = toKeys.Keys.Except(fromKeys.Keys)
                          .Select(k => TransitionSnapshot(toKeys[k], toOrderById)).ToList();
        var removed = fromKeys.Keys.Except(toKeys.Keys)
                          .Select(k => TransitionSnapshot(fromKeys[k], fromOrderById)).ToList();

        var changed = new List<TransitionChangedDto>();
        foreach (var key in fromKeys.Keys.Intersect(toKeys.Keys))
        {
            var ft = fromKeys[key];
            var tt = toKeys[key];
            var fieldChanges = new Dictionary<string, FieldChangedDto>();
            Check(fieldChanges, "conditionJson", ft.ConditionJson, tt.ConditionJson);
            Check(fieldChanges, "priority",      ft.Priority.ToString(), tt.Priority.ToString());
            if (fieldChanges.Count > 0)
                changed.Add(new TransitionChangedDto(key, fieldChanges));
        }

        return new TransitionsDiffDto(added, removed, changed);
    }

    private static string TransitionKey(
        WorkflowTransition t, Dictionary<Guid, int> orderById)
    {
        var fromOrder = orderById.TryGetValue(t.FromStateId, out var fo) ? fo.ToString() : "?";
        var toOrder   = t.ToStateId.HasValue && orderById.TryGetValue(t.ToStateId.Value, out var to)
                        ? to.ToString()
                        : t.ToTerminalStatus?.ToString() ?? "terminal";
        return $"{fromOrder}→{t.EventCode}→{toOrder}";
    }

    private static TransitionSnapshotDto TransitionSnapshot(
        WorkflowTransition t, Dictionary<Guid, int> orderById)
    {
        var fromOrder = orderById.TryGetValue(t.FromStateId, out var fo) ? (int?)fo : null;
        var toOrder   = t.ToStateId.HasValue && orderById.TryGetValue(t.ToStateId.Value, out var to)
                        ? (int?)to : null;
        return new TransitionSnapshotDto(
            fromOrder, toOrder, t.ToTerminalStatus?.ToString(),
            t.EventCode, t.Priority, t.ConditionJson, t.IsAutoGenerated);
    }
}

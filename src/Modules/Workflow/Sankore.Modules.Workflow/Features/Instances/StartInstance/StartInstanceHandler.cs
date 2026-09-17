using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Workflow;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.StartInstance;

internal sealed class StartInstanceHandler(
    WorkflowDbContext db,
    ICurrentUser currentUser,
    IRuleEvaluator evaluator,
    IEnumerable<IContextProvider> contextProviders
) : IRequestHandler<StartInstanceCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(StartInstanceCommand request, CancellationToken ct)
    {
        var template = await db.WorkflowTemplates
            .Include(t => t.Steps)
                .ThenInclude(s => s.Rules)
            .Include(t => t.Transitions)
            .FirstOrDefaultAsync(t => t.EntityType == request.EntityType && t.IsActive, ct);

        if (template is null)
            return Result.Fail<Guid>(
                $"No active workflow template found for entity type '{request.EntityType}'.");

        if (!template.Steps.Any())
            return Result.Fail<Guid>("The active template has no steps configured.");

        // Build context: provider-supplied base merged with caller overrides (caller wins).
        var context = await BuildContextAsync(
            request.EntityType, request.EntityId, currentUser.TenantId, request.Context, ct);

        var contextJson = context.Count > 0
            ? JsonSerializer.Serialize(context)
            : "{}";

        var rulesByStepDefId = template.Steps.ToDictionary(
            s => s.Id,
            s => (IReadOnlyCollection<WorkflowRule>)s.Rules);

        var instance = WorkflowInstance.Start(
            template, request.EntityId, currentUser.Id,
            contextJson, rulesByStepDefId, context, evaluator);

        db.WorkflowInstances.Add(instance);
        db.WorkflowInstanceSteps.AddRange(instance.Steps);
        await db.SaveChangesAsync(ct);

        return Result.Ok(instance.Id);
    }

    private async Task<Dictionary<string, object>> BuildContextAsync(
        string entityType,
        Guid entityId,
        Guid tenantId,
        Dictionary<string, object>? callerSupplied,
        CancellationToken ct)
    {
        // Start with provider-enriched context for the entity type (if one is registered).
        var provider = contextProviders.FirstOrDefault(
            p => p.EntityType.Equals(entityType, StringComparison.OrdinalIgnoreCase));

        var context = provider is not null
            ? await provider.BuildAsync(entityId, tenantId, ct)
            : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        // Merge caller-supplied values; caller keys override provider keys.
        if (callerSupplied is { Count: > 0 })
        {
            foreach (var (k, v) in callerSupplied)
                context[k] = v is JsonElement je ? ConvertElement(je) : v;
        }

        return context;
    }

    private static object ConvertElement(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.Number when je.TryGetDouble(out var d) => d,
        JsonValueKind.True  => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => je.EnumerateArray().Select(e => e.ToString()).ToArray(),
        _                   => je.GetString() ?? string.Empty
    };
}

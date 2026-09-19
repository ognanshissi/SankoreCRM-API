namespace Sankore.Modules.Leads.Features.TaskGenerationRules.ListTaskGenerationRules;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

internal sealed record ListTaskGenerationRulesQuery(bool? ActiveOnly = null)
    : IRequest<Result<IReadOnlyList<TaskGenerationRuleDto>>>;

public sealed record TaskGenerationRuleDto(
    Guid Id,
    Guid TenantId,
    string TriggerEventType,
    CrmTaskType TaskType,
    CrmTaskPriority Priority,
    string TitleTemplate,
    string? DescriptionTemplate,
    TimeSpan SlaDuration,
    TimeSpan DueDuration,
    bool IsActive,
    DateTimeOffset CreatedAt);

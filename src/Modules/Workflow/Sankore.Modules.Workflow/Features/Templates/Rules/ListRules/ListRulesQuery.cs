using MediatR;
using Sankore.Modules.Workflow.Domain;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Rules.ListRules;

public sealed record ListRulesQuery(Guid TemplateId, Guid StepId)
    : IRequest<Result<List<RuleDto>>>;

public sealed record RuleDto(
    Guid Id,
    RuleType RuleType,
    string Field,
    RuleOperator Operator,
    string Value,
    int LogicalGroup);

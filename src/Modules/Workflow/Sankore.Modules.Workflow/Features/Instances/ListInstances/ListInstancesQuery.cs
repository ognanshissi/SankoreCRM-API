using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.ListInstances;

public sealed record ListInstancesQuery(
    string? EntityType,
    Guid? EntityId,
    string? Status) : IRequest<Result<List<WorkflowInstanceDto>>>;

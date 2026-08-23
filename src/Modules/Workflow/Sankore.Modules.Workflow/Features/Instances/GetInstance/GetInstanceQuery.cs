using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.GetInstance;

public sealed record GetInstanceQuery(Guid InstanceId) : IRequest<Result<WorkflowInstanceDto>>;

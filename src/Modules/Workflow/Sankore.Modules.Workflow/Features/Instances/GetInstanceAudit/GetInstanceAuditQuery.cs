using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.GetInstanceAudit;

internal sealed record GetInstanceAuditQuery(Guid InstanceId) : IRequest<Result<List<WorkflowAuditEntryDto>>>;

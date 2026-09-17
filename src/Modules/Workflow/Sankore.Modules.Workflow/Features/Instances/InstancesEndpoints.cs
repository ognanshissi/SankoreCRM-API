using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Workflow.Features.Instances.ApproveStep;
using Sankore.Modules.Workflow.Features.Instances.AssignStep;
using Sankore.Modules.Workflow.Features.Instances.CancelInstance;
using Sankore.Modules.Workflow.Features.Instances.DelegateStep;
using Sankore.Modules.Workflow.Features.Instances.GetInstance;
using Sankore.Modules.Workflow.Features.Instances.GetInstanceAudit;
using Sankore.Modules.Workflow.Features.Instances.ListInstances;
using Sankore.Modules.Workflow.Features.Instances.ListMySteps;
using Sankore.Modules.Workflow.Features.Instances.RejectStep;
using Sankore.Modules.Workflow.Features.Instances.StartInstance;

namespace Sankore.Modules.Workflow.Features.Instances;

public static class InstancesEndpoints
{
    public static IEndpointRouteBuilder MapInstancesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("instances").WithTags("WorkflowInstances");

        group.MapListMySteps();

        return group
            .MapStartInstance()
            .MapListInstances()
            .MapGetInstance()
            .MapApproveStep()
            .MapRejectStep()
            .MapCancelInstance()
            .MapGetInstanceAudit()
            .MapAssignStep()
            .MapDelegateStep();
    }
}

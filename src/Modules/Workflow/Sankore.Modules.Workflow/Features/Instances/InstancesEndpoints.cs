using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Workflow.Features.Instances.ApproveStep;
using Sankore.Modules.Workflow.Features.Instances.CancelInstance;
using Sankore.Modules.Workflow.Features.Instances.GetInstance;
using Sankore.Modules.Workflow.Features.Instances.ListInstances;
using Sankore.Modules.Workflow.Features.Instances.RejectStep;
using Sankore.Modules.Workflow.Features.Instances.StartInstance;

namespace Sankore.Modules.Workflow.Features.Instances;

public static class InstancesEndpoints
{
    public static IEndpointRouteBuilder MapInstancesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("instances").WithTags("WorkflowInstances");

        return group
            .MapStartInstance()
            .MapListInstances()
            .MapGetInstance()
            .MapApproveStep()
            .MapRejectStep()
            .MapCancelInstance();
    }
}

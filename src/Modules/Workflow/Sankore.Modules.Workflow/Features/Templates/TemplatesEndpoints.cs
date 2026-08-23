using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Workflow.Features.Templates.ActivateTemplate;
using Sankore.Modules.Workflow.Features.Templates.AddStep;
using Sankore.Modules.Workflow.Features.Templates.CreateTemplate;
using Sankore.Modules.Workflow.Features.Templates.DeactivateTemplate;
using Sankore.Modules.Workflow.Features.Templates.GetTemplate;
using Sankore.Modules.Workflow.Features.Templates.ListTemplates;
using Sankore.Modules.Workflow.Features.Templates.RemoveStep;
using Sankore.Modules.Workflow.Features.Templates.UpdateTemplate;

namespace Sankore.Modules.Workflow.Features.Templates;

public static class TemplatesEndpoints
{
    public static IEndpointRouteBuilder MapTemplatesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("templates")
            .WithTags("WorkflowTemplates");

        return group
            .MapCreateTemplate()
            .MapListTemplates()
            .MapGetTemplate()
            .MapUpdateTemplate()
            .MapDeactivateTemplate()
            .MapActivateTemplate()
            .MapAddStep()
            .MapRemoveStep();
    }
}

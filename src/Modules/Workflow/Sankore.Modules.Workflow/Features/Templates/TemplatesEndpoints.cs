using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Workflow.Features.Templates.ActivateTemplate;
using Sankore.Modules.Workflow.Features.Templates.AddStep;
using Sankore.Modules.Workflow.Features.Templates.CreateDraft;
using Sankore.Modules.Workflow.Features.Templates.CreateTemplate;
using Sankore.Modules.Workflow.Features.Templates.DeactivateTemplate;
using Sankore.Modules.Workflow.Features.Templates.GetTemplate;
using Sankore.Modules.Workflow.Features.Templates.ListTemplates;
using Sankore.Modules.Workflow.Features.Templates.RemoveStep;
using Sankore.Modules.Workflow.Features.Templates.Rules.AddRule;
using Sankore.Modules.Workflow.Features.Templates.Rules.ListRules;
using Sankore.Modules.Workflow.Features.Templates.Rules.RemoveRule;
using Sankore.Modules.Workflow.Features.Templates.Transitions.AddAction;
using Sankore.Modules.Workflow.Features.Templates.Transitions.AddTransition;
using Sankore.Modules.Workflow.Features.Templates.Transitions.ListActions;
using Sankore.Modules.Workflow.Features.Templates.Transitions.ListTransitions;
using Sankore.Modules.Workflow.Features.Templates.Transitions.RemoveAction;
using Sankore.Modules.Workflow.Features.Templates.Transitions.RemoveTransition;
using Sankore.Modules.Workflow.Features.Templates.Triggers.AddTrigger;
using Sankore.Modules.Workflow.Features.Templates.Triggers.ListTriggers;
using Sankore.Modules.Workflow.Features.Templates.Triggers.RemoveTrigger;
using Sankore.Modules.Workflow.Features.Templates.GetTemplateDiff;
using Sankore.Modules.Workflow.Features.Templates.UpdateStep;
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
            .MapCreateDraft()
            .MapGetTemplateDiff()
            .MapListTemplates()
            .MapGetTemplate()
            .MapUpdateTemplate()
            .MapDeactivateTemplate()
            .MapActivateTemplate()
            .MapAddStep()
            .MapUpdateStep()
            .MapRemoveStep()
            .MapListRules()
            .MapAddRule()
            .MapRemoveRule()
            .MapListTransitions()
            .MapAddTransition()
            .MapRemoveTransition()
            .MapListActions()
            .MapAddAction()
            .MapRemoveAction()
            .MapListTriggers()
            .MapAddTrigger()
            .MapRemoveTrigger();
    }
}

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.CreateDraft;

internal static class CreateDraftEndpoint
{
    internal static IEndpointRouteBuilder MapCreateDraft(this IEndpointRouteBuilder app)
    {
        app.MapPost("{templateId:guid}/draft",
            async (Guid templateId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new CreateDraftCommand(templateId), ct);
                return result.IsSuccess
                    ? Results.Ok(new { draftTemplateId = result.Value })
                    : Results.BadRequest(new { error = result.Error });
            })
        .WithName("CreateWorkflowTemplateDraft")
        .WithSummary("Create a new draft version of an existing template (Version + 1)")
        .RequireAuthorization(Permissions.CanManageWorkflowSteps.Code)
        .Produces<object>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .WithOpenApi();

        return app;
    }
}

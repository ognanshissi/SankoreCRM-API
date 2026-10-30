using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Workflow;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.FormFields;

/// <summary>
/// The fields a « Formulaire » step can require for one entity type — the tenant's form (native
/// fields the owning module can check, and custom fields), from its <see cref="IWorkflowRecordGate"/>.
/// </summary>
internal static class ListFormFieldsEndpoint
{
    internal static IEndpointRouteBuilder MapListFormFields(this IEndpointRouteBuilder app)
    {
        app.MapGet("form-fields", async (string entityType, IEnumerable<IWorkflowRecordGate> gates, ICurrentUser user, CancellationToken ct) =>
            {
                var gate = gates.FirstOrDefault(g => string.Equals(g.EntityType, entityType, StringComparison.OrdinalIgnoreCase));
                var fields = gate is null ? [] : await gate.FieldsAsync(user.TenantId, ct);
                return Results.Ok(fields.Select(f => new FormFieldChoiceDto(f.Key, f.Label)).ToList());
            })
            .WithName("ListWorkflowFormFields")
            .WithSummary("The record fields a workflow form step can require, for one entity type")
            .RequireAuthorization(Permissions.CanManageWorkflowSteps.Code)
            .Produces<IReadOnlyList<FormFieldChoiceDto>>();

        return app;
    }
}

public sealed record FormFieldChoiceDto(string Key, string Label);

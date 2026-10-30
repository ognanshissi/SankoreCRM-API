using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Workflow;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.ConditionFields;

/// <summary>
/// The facts a « Condition » step may compare for one entity type — what the owning module's
/// <see cref="IContextProvider"/> fills. Empty for an entity no module describes (a KYC file): such a
/// circuit cannot hold a condition, and activation says so.
/// </summary>
internal static class ListConditionFieldsEndpoint
{
    internal static IEndpointRouteBuilder MapListConditionFields(this IEndpointRouteBuilder app)
    {
        app.MapGet("condition-fields", (string entityType, IEnumerable<IContextProvider> providers) =>
            {
                var provider = providers.FirstOrDefault(p =>
                    string.Equals(p.EntityType, entityType, StringComparison.OrdinalIgnoreCase));
                var fields = (provider?.Fields ?? [])
                    .Select(f => new ConditionFieldDto(f.Key, f.Label, f.Type,
                        f.Options?.Select(o => new ConditionFieldOptionDto(o.Value, o.Label)).ToList()))
                    .ToList();
                return Results.Ok(fields);
            })
            .WithName("ListWorkflowConditionFields")
            .WithSummary("The record facts a workflow condition step can compare, for one entity type")
            .RequireAuthorization(Permissions.CanManageWorkflowSteps.Code)
            .Produces<IReadOnlyList<ConditionFieldDto>>();

        return app;
    }
}

/// <param name="Type"><c>text</c>, <c>number</c>, <c>choice</c> or <c>boolean</c>.</param>
public sealed record ConditionFieldDto(string Key, string Label, string Type, IReadOnlyList<ConditionFieldOptionDto>? Options);

public sealed record ConditionFieldOptionDto(string Value, string Label);

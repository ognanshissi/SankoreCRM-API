using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Triggers.ListTriggerEvents;

/// <summary>
/// The internal events an <c>EntityEvent</c> trigger can name, for the editor's list — from
/// <see cref="PlatformEvents"/>, plus the capture event Leads publishes itself. Filtered by the
/// template's entity: an event on a client never starts a lead circuit (the consumer checks it).
/// </summary>
internal static class ListTriggerEventsEndpoint
{
    internal static IEndpointRouteBuilder MapListTriggerEvents(this IEndpointRouteBuilder app)
    {
        app.MapGet("trigger-events", (string? entityType) =>
            {
                var events = PlatformEvents.All
                    .Where(e => e.StartsWorkflows)
                    .Select(e => new TriggerEventDto(e.Code, e.Label, e.Description, e.Family, e.WorkflowEntityType))
                    .Prepend(new TriggerEventDto(
                        PlatformEvents.LegacyLeadCaptured, "Nouveau lead (capture)",
                        "Émis à la capture, avec le score, la source et le produit demandé dans son contexte. Les parcours existants l’utilisent.",
                        PlatformEvents.FamilyLead, "Lead"))
                    .Where(e => string.IsNullOrWhiteSpace(entityType)
                                || string.Equals(e.EntityType, entityType, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                return Results.Ok(events);
            })
            .WithName("ListWorkflowTriggerEvents")
            .WithSummary("The internal events a workflow trigger can name, optionally for one entity type")
            .RequireAuthorization(Permissions.CanManageWorkflowTriggers.Code)
            .Produces<IReadOnlyList<TriggerEventDto>>();

        return app;
    }
}

/// <param name="EntityType">The workflow entity it starts a circuit on: <c>Lead</c>, <c>Opportunity</c>, <c>Account</c>.</param>
public sealed record TriggerEventDto(string Code, string Label, string Description, string Family, string EntityType);

namespace Sankore.Modules.Leads.Features.ListLeads;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.GetLead;
using Sankore.Shared.Kernel;

internal sealed record ListLeadsQuery(
    int Page = 1,
    int PageSize = 20,
    LeadStatus? Status = null,
    PipelineStage? PipelineStage = null,
    LeadSource? Source = null,
    Guid? OwnerId = null,
    Guid? AgencyId = null,
    string? Search = null,
    string? Tag = null,
    LeadIntentLevel? IntentLevel = null,
    Guid? LeadSourceConfigId = null,
    bool? AwaitingDispatch = null,
    /// <summary>Custom-field filters, <c>key:value</c> — see <c>CustomFieldValues.FilterDocuments</c>.</summary>
    IReadOnlyList<string>? CustomFilters = null,
    /// <summary>
    /// These leads only (at most <see cref="MaxIds"/>) — a screen that holds ids from tasks or an agenda
    /// reads their leads in one call instead of one <c>GET leads/{id}</c> each.
    /// </summary>
    IReadOnlyList<Guid>? Ids = null,
    /// <summary>
    /// The leads this agent holds: assigned to them, or theirs as owner while nobody is assigned —
    /// the « Agent » column's reading. <see cref="OwnerId"/> alone missed every lead dispatched to an
    /// agent who is not its owner (« Mes leads », 2026-11-03).
    /// </summary>
    Guid? AgentId = null
) : IRequest<Result<PagedResult<LeadDto>>>
{
    public const int MaxIds = 100;

    public LeadListFilter Filter => new(
        Status, PipelineStage, Source, OwnerId, AgencyId, Search, Tag, IntentLevel, LeadSourceConfigId,
        AwaitingDispatch, CustomFilters, AgentId);
}

namespace Sankore.Modules.Leads.Features.Opportunities.History;

using System.Transactions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>A project's history, most recent first — the same perimeter as the project itself.</summary>
internal sealed record GetProjectHistoryQuery(Guid OpportunityId) : IRequest<Result<IReadOnlyList<ProjectHistoryDto>>>;

/// <param name="Kind">Created, StepChanged, AmountChanged, ProductChanged, CloseDateChanged, TemplateVersionChanged, Won, Lost, HoldingOpened, WorkflowRestarted.</param>
/// <param name="ActorName">Null for the system, and for a reconstructed line — whose author nobody recorded.</param>
/// <param name="From">See <see cref="ProjectHistoryEntry.From"/> for its shape per kind.</param>
public sealed record ProjectHistoryDto(
    Guid Id,
    ProjectHistoryKind Kind,
    DateTimeOffset At,
    Guid? ActorId,
    string? ActorName,
    string? From,
    string? To,
    string? Detail,
    Guid? RelatedId,
    bool Reconstructed);

internal sealed class GetProjectHistoryHandler(
    LeadsDbContext db,
    IAdministrationModule administration,
    ITenantContext tenant,
    LeadVisibility? visibility = null)
    : IRequestHandler<GetProjectHistoryQuery, Result<IReadOnlyList<ProjectHistoryDto>>>
{
    public async Task<Result<IReadOnlyList<ProjectHistoryDto>>> Handle(GetProjectHistoryQuery query, CancellationToken ct)
    {
        var projects = visibility?.Apply(db.Opportunities, db.Leads) ?? db.Opportunities;
        if (!await projects.AnyAsync(o => o.Id == query.OpportunityId, ct))
            return Result.Fail<IReadOnlyList<ProjectHistoryDto>>(OpportunityErrors.NotFound);

        var entries = await db.ProjectHistory.AsNoTracking()
            .Where(h => h.OpportunityId == query.OpportunityId)
            .OrderByDescending(h => h.At)
            .ToListAsync(ct);

        var actors = entries.Select(h => h.ActorId).OfType<Guid>().Distinct().ToList();
        IReadOnlyDictionary<Guid, string> names = new Dictionary<Guid, string>();
        if (actors.Count > 0)
        {
            using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
            names = await administration.GetUserNamesAsync(tenant.CurrentTenantId, actors, ct);
        }

        return Result.Ok<IReadOnlyList<ProjectHistoryDto>>(entries
            .Select(h => new ProjectHistoryDto(
                h.Id, h.Kind, h.At, h.ActorId,
                h.ActorId is { } actor ? names.GetValueOrDefault(actor, "Utilisateur inconnu") : null,
                h.From, h.To, h.Detail, h.RelatedId, h.Reconstructed))
            .ToList());
    }
}

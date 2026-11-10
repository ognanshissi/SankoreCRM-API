namespace Sankore.Modules.Leads.Features.ListLeads;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// What each view of the lead list would show (2026-11-03): the tabs fix status, temperature,
/// « mine » or the queue, so the server answers those axes and the screen reads its views off them.
/// </summary>
/// <param name="Total">« Tous les leads ».</param>
/// <param name="Mine">The caller's leads: assigned to them, or theirs as owner while nobody is assigned.</param>
/// <param name="AwaitingDispatch">« En file d'attente ».</param>
/// <param name="ByStatus">Per <see cref="LeadStatus"/> name; a status with no lead is absent.</param>
/// <param name="ByIntent">Per <see cref="LeadIntentLevel"/> name; a temperature with no lead is absent.</param>
public sealed record LeadViewCountsDto(
    int Total,
    int Mine,
    int AwaitingDispatch,
    IReadOnlyDictionary<string, int> ByStatus,
    IReadOnlyDictionary<string, int> ByIntent,
    /// <summary>« Grands comptes » (key accounts lot 4): company prospects at the « grand compte » threshold.</summary>
    int KeyAccounts = 0);

/// <summary>
/// The filters a view leaves in place — agency, source, search, custom fields — so the counts match
/// what a tab opens on. Status, temperature, agent and queue are the views themselves.
/// </summary>
internal sealed record GetLeadViewCountsQuery(
    Guid? AgencyId = null,
    string? Search = null,
    Guid? LeadSourceConfigId = null,
    IReadOnlyList<string>? CustomFilters = null,
    LeadType? ProspectType = null,
    string? ActivitySector = null,
    LeadCompanySize? CompanySize = null) : IRequest<Result<LeadViewCountsDto>>;

internal sealed class GetLeadViewCountsHandler(
    LeadsDbContext db,
    ICurrentUser currentUser,
    Sankore.Modules.Administration.PublicApi.IAdministrationModule? administration = null,
    ITenantContext? tenant = null,
    LeadVisibility? visibility = null)
    : IRequestHandler<GetLeadViewCountsQuery, Result<LeadViewCountsDto>>
{
    public async Task<Result<LeadViewCountsDto>> Handle(GetLeadViewCountsQuery query, CancellationToken ct)
    {
        // The list's own visibility and filters: a count the list would not reach is a wrong count.
        var visible = visibility?.Apply(db.Leads.AsNoTracking()) ?? db.Leads.AsNoTracking();
        var filtered = await new LeadListFilter(
                AgencyId: query.AgencyId, Search: query.Search, LeadSourceConfigId: query.LeadSourceConfigId,
                CustomFilters: query.CustomFilters, ProspectType: query.ProspectType,
                ActivitySector: query.ActivitySector, CompanySize: query.CompanySize)
            .ApplyAsync(visible, db, administration, tenant, ct);
        if (filtered.IsFailure)
            return Result.Fail<LeadViewCountsDto>(filtered.Error!);
        var q = filtered.Value;

        var byStatus = (await q.GroupBy(l => l.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
            .ToDictionary(g => g.Key.ToString(), g => g.Count);
        var byIntent = (await q.GroupBy(l => l.IntentLevel).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
            .ToDictionary(g => g.Key.ToString(), g => g.Count);

        var me = currentUser.Id;
        var mine = me == Guid.Empty ? 0
            : await q.CountAsync(l => l.CurrentAssignedId == me || (l.CurrentAssignedId == null && l.OwnerId == me), ct);
        var queue = await q.CountAsync(l => l.AwaitingDispatchSince != null && l.CurrentAssignmentId == null, ct);

        var keyAccounts = tenant is null ? 0
            : await (await Settings.KeyAccounts.KeyAccountSettings.ReadAsync(db, tenant.CurrentTenantId, ct))
                .WhereTier(q, LeadPotentialTier.KeyAccount).CountAsync(ct);

        return Result.Ok(new LeadViewCountsDto(byStatus.Values.Sum(), mine, queue, byStatus, byIntent, keyAccounts));
    }
}

internal static class GetLeadViewCountsEndpoint
{
    public static IEndpointRouteBuilder MapGetLeadViewCounts(this IEndpointRouteBuilder app)
    {
        app.MapGet("view-counts", async (
                ISender sender,
                CancellationToken ct,
                Guid? agencyId = null,
                string? search = null,
                Guid? leadSourceConfigId = null,
                // Repeated, as on the list: `cf=segment:Commercant&cf=vip:oui`.
                [Microsoft.AspNetCore.Mvc.FromQuery(Name = "cf")] string[]? customFilters = null,
                LeadType? prospectType = null,
                string? activitySector = null,
                LeadCompanySize? companySize = null) =>
            {
                var result = await sender.Send(new GetLeadViewCountsQuery(
                    agencyId, search, leadSourceConfigId, customFilters, prospectType, activitySector, companySize), ct);
                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.Problem(title: "Invalid filter", detail: result.Error, statusCode: StatusCodes.Status400BadRequest);
            })
            .WithName("GetLeadViewCounts")
            .WithTags("Leads")
            .WithSummary("How many leads each view of the list holds, under the filters the views share")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<LeadViewCountsDto>()
            .WithOpenApi();
        return app;
    }
}

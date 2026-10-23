namespace Sankore.Modules.Leads.Features.Visibility;

using System.Linq.Expressions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// A read of ONE lead, by id. <see cref="LeadVisibilityBehavior{TRequest,TResponse}"/> answers
/// <c>LEAD_NOT_FOUND</c> before the handler runs when the caller may not see that lead — so a new
/// read slice is covered by declaring the interface, not by remembering a check.
/// </summary>
internal interface ILeadReadRequest
{
    Guid LeadId { get; }
}

/// <summary>
/// Which leads a caller sees. Without <c>lead:read:all</c> (Agent, CommercialAgent): the leads they
/// hold — the current assignee or the owner — and, while nobody holds a lead, the one they
/// collected. With it: every lead of the tenant, as before.
///
/// <para>
/// Narrower than <see cref="Household.LeadAccessGuard"/>, which also lets a team's supervisor work
/// a lead: a supervisor sees through the permission, which their roles receive by default.
/// </para>
/// </summary>
public sealed class LeadVisibility(ICurrentUser currentUser)
{
    public bool SeesAll => RecordVisibility.SeesAll(currentUser, Permissions.CanReadAllLeads);

    /// <summary>The caller's leads only, as a SQL predicate — a list filters before it counts.</summary>
    public IQueryable<Lead> Apply(IQueryable<Lead> leads)
        => SeesAll ? leads : leads.Where(AssignedTo(currentUser.Id));

    /// <summary>
    /// An opportunity follows its lead; one with no lead (opened on a client) is its owner's.
    /// </summary>
    public IQueryable<Opportunity> Apply(IQueryable<Opportunity> opportunities, IQueryable<Lead> leads)
    {
        if (SeesAll)
            return opportunities;

        var me = currentUser.Id;
        var mine = leads.Where(AssignedTo(me));
        return opportunities.Where(o => o.OwnerId == me || (o.LeadId != null && mine.Any(l => l.Id == o.LeadId)));
    }

    internal static Expression<Func<Lead, bool>> AssignedTo(Guid me)
        => l => l.CurrentAssignedId == me
                || l.OwnerId == me
                || (l.CurrentAssignedId == null && l.OwnerId == null && l.AgentCollectedLeadId == me);
}

/// <summary>
/// Refuses an <see cref="ILeadReadRequest"/> on a lead outside the caller's visibility with
/// <c>LEAD_NOT_FOUND</c> — never a 403, which would confirm that the lead exists.
/// </summary>
internal sealed class LeadVisibilityBehavior<TRequest, TResponse>(IServiceProvider services)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private const string NotFound = "LEAD_NOT_FOUND";

    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ILeadReadRequest read)
            return await next();

        // Resolved here and not injected: this behavior sits in front of every request of every
        // module, and only a lead read needs a LeadsDbContext.
        var visibility = services.GetRequiredService<LeadVisibility>();
        if (visibility.SeesAll)
            return await next();

        var me = services.GetRequiredService<ICurrentUser>().Id;
        var visible = await services.GetRequiredService<LeadsDbContext>().Leads
            .AsNoTracking()
            .Where(l => l.Id == read.LeadId)
            .AnyAsync(LeadVisibility.AssignedTo(me), cancellationToken);

        return visible ? await next() : ResultFailures.Of<TResponse>(NotFound);
    }
}

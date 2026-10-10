using Microsoft.AspNetCore.Identity;

namespace Sankore.Modules.Administration.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// The single door into the Users module for the rest of the system.
/// Internal on purpose: consumers depend on IAdministrationModule (PublicApi),
/// never on this class or on AdministrationDbContext directly.
/// </summary>
internal sealed class AdministrationModuleFacade(AdministrationDbContext db) : IAdministrationModule
{
    /// <summary>
    /// Roles that put a user in the lead-dispatch pool.
    ///
    /// <para>
    /// BOTH agent roles, not only <c>CommercialAgent</c>: <c>RoleSeeder</c> seeds the two, nothing
    /// in this solution distinguishes them, and which one an administrator picked when creating a
    /// commercial team is not something the dispatcher should depend on — the symptom of guessing
    /// wrong is <c>NO_AGENT_AVAILABLE</c> on every lead of a tenant whose agents all hold the other
    /// one. Narrowing the pool is a one-line change here, and it is the only place to make it.
    /// </para>
    /// </summary>
    private static readonly string[] DispatchableRoleCodes =
    [
        Roles.CommercialAgent.Code,
        Roles.Agent.Code,
    ];

    /// <summary>
    /// The agents M13 may route a lead to. "Available" is four conditions, not one — see below —
    /// and the caller is told nothing about which failed: <c>DispatchLeadHandler</c> reports a
    /// single <c>AGENT_NOT_ELIGIBLE</c> precisely because the reason belongs to this module.
    ///
    /// <para>
    /// <paramref name="agencyId"/> is the lead's <c>PreferredAgencyId</c> and matches the agency
    /// EXACTLY — not its subtree. Widening it to descendants would route a lead to a counter of
    /// another branch while the screen says the lead is preferred at this one, which is a product
    /// decision and not a detail of this query. A preferred agency that holds no agent of its own
    /// (a head office, typically) therefore yields an empty pool, and the handler publishes
    /// <c>LeadDispatchingFailedEvent(NO_AGENT_AVAILABLE)</c> — visible, rather than silently
    /// dispatched somewhere else.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<AgentSummary>> GetAvailableAgentsAsync(
        Guid tenantId, Guid? agencyId, CancellationToken ct)
    {
        // Role membership is read from the tenant-scoped MIRROR (db.UserRoles, table user_roles),
        // not from Identity's join table. The mirror is the only one of the two that carries a
        // TenantId and a soft revoke: reading Identity's would keep handing leads to an agent whose
        // grant was revoked, since a revoke only flips IsActive here. Every grant path writes both
        // stores — see the DbSet's own remark.
        //
        // Materialised as a List: on .NET 10 an array's Contains binds to the ReadOnlySpan<T>
        // extension and no longer translates to SQL.
        var dispatchableRoles = DispatchableRoleCodes.ToList();

        var agentIds = await db.UserRoles
            .IgnoreQueryFilters()
            .Where(ur => ur.TenantId == tenantId && ur.IsActive)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .Where(x => x.Name != null && dispatchableRoles.Contains(x.Name))
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(ct);

        if (agentIds.Count == 0)
            return [];

        // IgnoreQueryFilters plus an explicit tenant predicate, like every other method here: the
        // callers are a MediatR handler, a MassTransit consumer and a Hangfire job, and the ambient
        // ITenantContext is not necessarily the tenant being asked about.
        var query = db.Users
            .IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId
                     && agentIds.Contains(u.Id)
                     // The explicit "I am taking leads" flag (AppUser.SetAvailability).
                     && u.IsAvailable
                     // Active only: a PendingActivation account has never logged in, and a Disabled
                     // or Locked one cannot. Assigning a lead to any of them parks it on somebody
                     // who will not see it, and the SLA clock starts anyway.
                     && u.Status == UserStatus.Active
                     // A root account and a technical account are not a commercial team, even when
                     // somebody granted them an agent role.
                     && !u.IsSuperUser
                     && u.AccountType == UserAccountType.Standard
                     // No agency, no routing: AgentSummary flattens a null agency to Guid.Empty,
                     // which the geographic and agency-matching scorers would read as a real
                     // agency. Excluded rather than summarised wrongly.
                     && u.AgencyId != null);

        if (agencyId.HasValue)
            query = query.Where(u => u.AgencyId == agencyId.Value);

        // Ordered by id so the pool is stable between two calls. The strategies rank it themselves,
        // but they rank on load and conversion rate, where ties are common — an unordered pool makes
        // a tie resolve differently on each dispatch, which is impossible to reproduce.
        var agents = await query.OrderBy(u => u.Id).ToListAsync(ct);

        return agents.Select(ToSummary).ToList();
    }

    /// <summary>
    /// Retried on a concurrency clash rather than failed: two outbox instances hitting the same
    /// tenant in the same millisecond is ordinary, and losing an email over it would be absurd.
    /// The retry re-reads, so the second attempt decides against the winner's count.
    /// </summary>
    private const int QuotaRetries = 3;

    public async Task<EmailQuotaDecision> TryConsumeEmailQuotaAsync(Guid tenantId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < QuotaRetries; attempt++)
        {
            var settings = await db.TenantNotificationSettings
                .AsTracking()
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

            // A tenant that never opened the settings screen has no limit to enforce.
            if (settings is null)
                return new EmailQuotaDecision(Granted: true, Limit: null, UsedThisMonth: 0);

            var granted = settings.TryConsumeMonthlyQuota();

            if (!granted)
            {
                return new EmailQuotaDecision(
                    false, settings.MonthlyQuotaLimit, settings.CurrentMonthUsageCount);
            }

            try
            {
                await db.SaveChangesAsync(ct);

                return new EmailQuotaDecision(
                    true, settings.MonthlyQuotaLimit, settings.CurrentMonthUsageCount);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Someone else counted first. Drop the stale entity and read the new truth.
                db.ChangeTracker.Clear();
            }
        }

        // Persistent contention: refuse rather than send uncounted. The outbox will retry.
        return new EmailQuotaDecision(Granted: false, Limit: null, UsedThisMonth: 0);
    }

    public async Task<TenantNotificationConfigDto?> GetNotificationConfigAsync(
        Guid tenantId, CancellationToken ct)
    {
        var s = await db.TenantNotificationSettings
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);

        return s is null ? null : new TenantNotificationConfigDto(
            s.ProviderType,
            s.UseDefaultPlatformProvider,
            s.FromEmail,
            s.FromName,
            s.ReplyToEmail,
            s.SendingDomain,
            s.MonthlyQuotaLimit,
            s.HasCredential,
            s.SmtpHost,
            s.SmtpPort,
            s.SmtpUsername,
            s.SmtpUseSsl,
            s.SmtpUseStartTls);
    }

    public async Task<string?> GetProductCategoryAsync(
        Guid tenantId, string productCode, CancellationToken ct)
    {
        var product = await db.ProductSpecialities
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId
                                   && p.Code == productCode.ToUpperInvariant(), ct);

        return product?.Category.ToString();
    }

    /// <summary>
    /// <c>IgnoreQueryFilters</c> plus an explicit tenant predicate, like every other method here:
    /// the caller may be a Hangfire job or a MassTransit consumer, where the ambient tenant is not
    /// the tenant being asked about.
    /// </summary>
    public async Task<ProductSummary?> GetProductAsync(
        Guid tenantId, string productCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(productCode)) return null;

        var code = productCode.Trim().ToUpperInvariant();

        return await db.ProductSpecialities
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId && p.Code == code)
            .Select(p => new ProductSummary(
                p.Code, p.Name, p.Category.ToString(), p.IsActive))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ProductSummary>> ListProductsAsync(
        Guid tenantId, string? category, CancellationToken ct)
    {
        var query = db.ProductSpecialities
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(category))
        {
            // An unparseable category answers an empty list and never the whole catalogue: a
            // filter quietly dropped is how a caller asking for insurance products gets loans.
            if (!Enum.TryParse<ProductCategory>(category, ignoreCase: true, out var parsed))
                return [];

            query = query.Where(p => p.Category == parsed);
        }

        return await query
            .OrderBy(p => p.Code)
            .Select(p => new ProductSummary(
                p.Code, p.Name, p.Category.ToString(), p.IsActive))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetTeamAgentIdsAsync(
        Guid tenantId, Guid supervisorId, CancellationToken ct)
    {
        var supervisor = await db.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == supervisorId && u.TenantId == tenantId, ct);

        if (supervisor is null)
            return Array.Empty<Guid>();

        // Super-users see all agents for the tenant.
        if (supervisor.IsSuperUser || supervisor.AgencyId is null)
        {
            return await db.Users
                .IgnoreQueryFilters()
                .Where(u => u.TenantId == tenantId && u.Id != supervisorId)
                .Select(u => u.Id)
                .ToListAsync(ct);
        }

        // Collect the supervisor's agency and all descendant agencies.
        var teamAgencyIds = new HashSet<Guid> { supervisor.AgencyId.Value };
        var frontier = new Queue<Guid>();
        frontier.Enqueue(supervisor.AgencyId.Value);

        while (frontier.Count > 0)
        {
            var parentId = frontier.Dequeue();
            var children = await db.Agencies
                .IgnoreQueryFilters()
                .Where(a => a.TenantId == tenantId && a.ParentAgencyId == parentId && a.IsActive)
                .Select(a => a.Id)
                .ToListAsync(ct);

            foreach (var childId in children)
            {
                if (teamAgencyIds.Add(childId))
                    frontier.Enqueue(childId);
            }
        }

        return await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId
                     && u.AgencyId.HasValue
                     && teamAgencyIds.Contains(u.AgencyId.Value)
                     && u.Id != supervisorId)
            .Select(u => u.Id)
            .ToListAsync(ct);
    }

    public async Task<AgencySummary?> GetAgencyAsync(Guid tenantId, Guid agencyId, CancellationToken ct)
    {
        // IgnoreQueryFilters + an explicit tenant predicate: callers may be a Hangfire
        // job or a MassTransit consumer, where the ambient ITenantContext is not the
        // tenant being read. Soft-deleted agencies are invisible on purpose — a client
        // must never be attached to an agency that no longer exists operationally.
        return await db.Agencies
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && a.Id == agencyId && !a.IsDeleted)
            .Select(a => new AgencySummary(a.Id, a.Code, a.Name, a.ParentAgencyId, a.IsActive))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<AgentSummary?> GetAgentAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await db.Users.SingleOrDefaultAsync(u => u.Id == agentId, ct);
        return agent is null ? null : ToSummary(agent);
    }

    private static AgentSummary ToSummary(AppUser u) => new(
        Id: u.Id,
        FullName: u.FullName,
        AgencyId: u.AgencyId ?? Guid.Empty,
        SpokenLanguages: u.SpokenLanguages,
        Specialties: u.Specialties,
        CurrentLocation: u.LastKnownLocation,
        ActiveLeadsCount: u.ActiveLeadsCount,
        HotLeadsCount: u.HotLeadsCount,
        ConversionRate30d: u.ConversionRate30D,
        IsAvailable: u.IsAvailable);
}

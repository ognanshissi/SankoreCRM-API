using Microsoft.AspNetCore.Identity;

namespace Sankore.Modules.Administration.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Compliance;

/// <summary>
/// The single door into the Users module for the rest of the system.
/// Internal on purpose: consumers depend on IAdministrationModule (PublicApi),
/// never on this class or on AdministrationDbContext directly.
/// </summary>
/// <param name="emailSender">
/// Optional only for the sake of unit tests, which construct this facade directly and in the
/// main do not notify anybody — the same arrangement <c>AgentExclusionService</c>'s cache uses.
/// DI always supplies it; <see cref="NotifyUserAsync"/> throws rather than quietly sending
/// nothing when it is absent, because a notification silently disabled is the failure this whole
/// change exists to remove.
/// </param>
internal sealed class AdministrationModuleFacade(
    AdministrationDbContext db,
    ProductCatalogCache catalogCache,
    ModuleEmailSender? emailSender = null)
    : IAdministrationModule, IDistributionComplianceProvider
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
        if (agents.Count == 0)
            return [];

        // Ce que l'agent a déclaré lui-même : statut (pause, absence datée), horaires et capacité.
        // `IsAvailable` reste l'interrupteur de l'administration ; les deux doivent laisser passer.
        // Filtré en mémoire : les règles (période d'absence, plage horaire du jour) ne se traduisent
        // pas en SQL, et le vivier d'une agence se compte en dizaines. Un agent sans paramétrage n'a
        // déclaré aucune contrainte et reste dispatchable, comme avant l'écran.
        var ids = agents.Select(a => a.Id).ToList();
        var settings = await db.AgentSettings
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && ids.Contains(s.UserId))
            .ToDictionaryAsync(s => s.UserId, ct);

        // Le fuseau du profil situe les horaires et les dates d'absence de chaque agent.
        var settledIds = settings.Keys.ToList();
        var timeZones = settledIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.UserProfiles
                .IgnoreQueryFilters()
                .Where(p => p.TenantId == tenantId && settledIds.Contains(p.UserId))
                .ToDictionaryAsync(p => p.UserId, p => p.TimeZone, ct);

        var now = DateTimeOffset.UtcNow;
        return agents
            .Where(a => !settings.TryGetValue(a.Id, out var s)
                        || s.IsDispatchable(now, a.ActiveLeadsCount,
                            UserProfile.ResolveTimeZone(timeZones.GetValueOrDefault(a.Id))))
            .Select(ToSummary)
            .ToList();
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

    /// <summary>
    /// Notifies one of the tenant's users. The address never leaves this module — see the
    /// contract's own remarks for why that is the point rather than an accident.
    /// </summary>
    public async Task<bool> NotifyUserAsync(NotifyUserRequest request, CancellationToken ct)
    {
        if (emailSender is null)
        {
            throw new InvalidOperationException(
                $"{nameof(AdministrationModuleFacade)} was built without a "
                + $"{nameof(ModuleEmailSender)}, so it cannot notify user {request.UserId}. "
                + "DI supplies one; a test calling this must pass it.");
        }

        // IgnoreQueryFilters paired with an explicit tenant predicate, the rule this file follows
        // throughout: the callers are consumers and jobs, whose ambient tenant is not necessarily
        // the tenant being notified.
        var user = await db.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.TenantId == request.TenantId && u.Id == request.UserId, ct);

        if (user is null)
        {
            // False and not an exception: the caller is a consumer whose own work has already
            // happened. A deactivated or deleted recipient is an ordinary outcome.
            return false;
        }

        return await emailSender.SendAsync(
            request.TenantId,
            user,
            request.TemplateKey,
            request.IdempotencyKey,
            request.TemplateData,
            ct);
    }

    /// <summary>
    /// The holder's current credential, read as an eligibility. One indexed read on
    /// <c>(tenant_id, user_id) WHERE valid_to IS NULL</c>.
    /// </summary>
    /// <summary>
    /// Whether a product line is an insurance line. Through the SAME cached catalogue read the
    /// beneficiary policy goes through — five minutes, per line — so the professional-card guard
    /// costs no extra query on the overwhelming majority of dispatches, which are not insurance.
    /// </summary>
    public async Task<bool> IsInsuranceProductLineAsync(
        Guid tenantId, Guid productLineId, CancellationToken ct)
    {
        var policy = await GetBeneficiaryPolicyAsync(tenantId, productLineId, ct);

        // ProductCategories.IsInsurance answers false for a dangling line, which is the
        // convention every opaque reference in this solution follows: a product somebody removed
        // must not make a lead unworkable.
        return ProductCategories.IsInsurance(policy?.Category);
    }

    public async Task<DistributionEligibilitySnapshot> GetDistributionEligibilityAsync(
        Guid tenantId, Guid userId, DateOnly asOf, CancellationToken ct)
    {
        // IgnoreQueryFilters paired with an explicit tenant predicate, the rule this file follows
        // throughout: the callers are a dispatching handler, a subscription gate and a consumer,
        // and the last of those has no ambient tenant of its own.
        var current = await db.DistributionCredentials
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.UserId == userId && c.ValidTo == null)
            .FirstOrDefaultAsync(ct);

        if (current is not null)
            return Describe(current, asOf);

        // No current row. Distinguishing "never recorded" from "withdrawn" costs one more read
        // and is what lets a screen tell a tenant that has not started recording cards from an
        // agent whose licence was taken away.
        var hadOne = await db.DistributionCredentials
            .IgnoreQueryFilters()
            .AnyAsync(c => c.TenantId == tenantId && c.UserId == userId, ct);

        return hadOne
            ? new DistributionEligibilitySnapshot(DistributionEligibility.Withdrawn, null, false, null)
            : DistributionEligibilitySnapshot.NotRecorded;
    }

    /// <summary>
    /// The batched form, for a dispatching pool. Two queries whatever the pool size, and every
    /// id asked about is present in the answer.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, DistributionEligibilitySnapshot>>
        GetDistributionEligibilitiesAsync(
            Guid tenantId, IReadOnlyCollection<Guid> userIds, DateOnly asOf, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        if (userIds.Count == 0)
            return new Dictionary<Guid, DistributionEligibilitySnapshot>();

        // A List and not the collection as given: on .NET 10 an array's Contains binds to the
        // ReadOnlySpan<T> extension and no longer translates to SQL.
        var ids = userIds.Distinct().ToList();

        var current = await db.DistributionCredentials
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && ids.Contains(c.UserId) && c.ValidTo == null)
            .ToListAsync(ct);

        var currentByUser = current.ToDictionary(c => c.UserId);

        // Which of the remaining ids have a CLOSED credential, so a withdrawal is not reported as
        // "never recorded". One query for the whole remainder rather than one per user.
        var withoutCurrent = ids.Where(id => !currentByUser.ContainsKey(id)).ToList();

        var everHad = withoutCurrent.Count == 0
            ? []
            : await db.DistributionCredentials
                .IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId && withoutCurrent.Contains(c.UserId))
                .Select(c => c.UserId)
                .Distinct()
                .ToListAsync(ct);

        var withdrawn = everHad.ToHashSet();

        return ids.ToDictionary(
            id => id,
            id => currentByUser.TryGetValue(id, out var credential)
                ? Describe(credential, asOf)
                : withdrawn.Contains(id)
                    ? new DistributionEligibilitySnapshot(DistributionEligibility.Withdrawn, null, false, null)
                    : DistributionEligibilitySnapshot.NotRecorded);
    }

    /// <summary>
    /// Projects a current credential. The validity question goes through
    /// <c>DistributionCredential.IsValidAt</c> rather than being re-derived here — it is the
    /// single definition, shared with the expiry report, and a second comparison in this file is
    /// how the two would come to disagree about a lapsed indemnity cover.
    /// </summary>
    private static DistributionEligibilitySnapshot Describe(DistributionCredential credential, DateOnly asOf)
        => new(
            credential.IsValidAt(asOf) ? DistributionEligibility.Valid : DistributionEligibility.Expired,
            credential.ExpiresOn,
            credential.HasIndemnityCover,
            credential.TrainingHours);

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
                p.Code, p.Name, p.Category.ToString(), p.IsActive, p.Id, p.AllowMultipleHoldings))
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
                p.Code, p.Name, p.Category.ToString(), p.IsActive, p.Id, p.AllowMultipleHoldings))
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

    public async Task<BeneficiaryPolicyDto?> GetBeneficiaryPolicyAsync(
        Guid tenantId, Guid productLineId, CancellationToken ct)
    {
        if (productLineId == Guid.Empty)
            return null;

        var key = ProductCatalogCache.PolicyKey(tenantId, productLineId);

        var cached = await catalogCache.GetAsync<BeneficiaryPolicyDto>(key, ct);
        if (cached is not null)
            return cached;

        // IgnoreQueryFilters + explicit tenant predicate: M13's completeness check and M01's
        // designation rules run from consumers and jobs with no ambient tenant.
        var line = await db.ProductSpecialities
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId && p.Id == productLineId)
            .Select(p => new
            {
                p.Id, p.Code, p.Category, p.IsActive, p.AllowMultipleHoldings, p.BeneficiaryPolicy
            })
            .FirstOrDefaultAsync(ct);

        if (line is null)
            return null;   // dangling reference — NOT the same as "asks for nothing"

        var policy = line.BeneficiaryPolicy;

        var dto = new BeneficiaryPolicyDto(
            ProductLineId:         line.Id,
            ProductCode:           line.Code,
            Category:              line.Category.ToString(),
            IsActive:              line.IsActive,
            AllowMultipleHoldings: line.AllowMultipleHoldings,
            AllowedRoles:          [.. policy.Roles.Select(r => new DesignationRoleDto(
                                        r.Code, r.Label, r.MinCount, r.MaxCount, r.SharesMustTotal100))],
            MaxChildren:           policy.MaxChildren,
            ChildMaxAge:           policy.ChildMaxAge,
            RequiredFieldsBeforeConversion: policy.RequiredFields,
            RequiredKycTier:       policy.RequiredKycTier,
            IntegrationSystem:     policy.IntegrationSystem);

        await catalogCache.SetAsync(key, dto, ct);
        return dto;
    }

    public async Task<KycRemotePolicyDto?> GetKycRemotePolicyAsync(
        Guid tenantId, Guid productLineId, CancellationToken ct)
    {
        if (productLineId == Guid.Empty)
            return null;

        var key = ProductCatalogCache.RemoteKycPolicyKey(tenantId, productLineId);

        var cached = await catalogCache.GetAsync<KycRemotePolicyDto>(key, ct);
        if (cached is not null)
            return cached;

        // IgnoreQueryFilters + explicit tenant predicate: the caller is M02's consumer of the
        // conversion event, which has no ambient tenant of its own.
        var line = await db.ProductSpecialities
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId && p.Id == productLineId)
            .Select(p => new
            {
                p.Id, p.Code, p.Name, p.Category, p.IsActive, p.KycRemotePolicy
            })
            .FirstOrDefaultAsync(ct);

        if (line is null)
            return null;   // dangling reference — NOT the same as "forbids remote KYC"

        var policy = line.KycRemotePolicy;

        var dto = new KycRemotePolicyDto(
            ProductLineId:        line.Id,
            ProductCode:          line.Code,
            ProductName:          line.Name,
            Category:             line.Category.ToString(),
            IsActive:             line.IsActive,
            Enabled:              policy.Enabled,
            ValidationMode:       policy.ValidationMode,
            RequiredTier:         policy.RequiredTier,
            MinAutoScore:         policy.MinAutoScore,
            RequiredDocuments:    policy.Documents,
            AllowedDocumentTypes: policy.DocumentTypes,
            LinkValidityHours:    policy.LinkValidityHours);

        await catalogCache.SetAsync(key, dto, ct);
        return dto;
    }

    public async Task<Guid?> ResolveProductLineAsync(
        Guid tenantId, string? label, CancellationToken ct)
    {
        var probe = ProductLabelNormalizer.Normalize(label);
        if (probe is null)
            return null;

        var catalogue = await ActiveCatalogueAsync(tenantId, ct);

        // Code first, then name. A code is an identifier an administrator chose; a name is a label
        // that two lines could plausibly share, and when they do the ambiguity must not be resolved
        // by row order — so a duplicate name resolves to NOTHING and an agent picks the line.
        var byCode = catalogue.Where(e => e.NormalizedCode == probe).ToList();
        if (byCode.Count == 1)
            return byCode[0].Id;
        if (byCode.Count > 1)
            return null;

        var byName = catalogue.Where(e => e.NormalizedName == probe).ToList();
        return byName.Count == 1 ? byName[0].Id : null;
    }

    /// <summary>
    /// The tenant's ACTIVE lines, normalised for matching and cached for five minutes.
    ///
    /// <para>
    /// Loaded whole and matched in memory rather than compared in SQL, because the comparison has
    /// to be accent-insensitive — "CREDIT COMMERCANT" must find "Crédit commerçant" — and
    /// PostgreSQL cannot do that without <c>unaccent</c> and a functional index on both columns.
    /// A tenant's catalogue is tens of rows; the cache makes it one query per five minutes, which
    /// matters because the capture path calls this on every lead.
    /// </para>
    ///
    /// <para>
    /// Retired lines are excluded: resolving a label onto a product that is no longer sold would
    /// apply its policy to a brand-new request.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<CatalogueEntry>> ActiveCatalogueAsync(
        Guid tenantId, CancellationToken ct)
    {
        var key = ProductCatalogCache.CatalogueKey(tenantId);

        var cached = await catalogCache.GetAsync<List<CatalogueEntry>>(key, ct);
        if (cached is not null)
            return cached;

        var rows = await db.ProductSpecialities
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId && p.IsActive)
            .Select(p => new { p.Id, p.Code, p.Name })
            .ToListAsync(ct);

        var catalogue = rows
            .Select(r => new CatalogueEntry(
                r.Id,
                ProductLabelNormalizer.Normalize(r.Code) ?? string.Empty,
                ProductLabelNormalizer.Normalize(r.Name) ?? string.Empty))
            .ToList();

        await catalogCache.SetAsync(key, catalogue, ct);
        return catalogue;
    }

    /// <summary>Public for the cache's serializer only; not part of any contract.</summary>
    internal sealed record CatalogueEntry(Guid Id, string NormalizedCode, string NormalizedName);

    public async Task<ComplianceSettingsDto> GetComplianceSettingsAsync(Guid tenantId, CancellationToken ct)
    {
        // IgnoreQueryFilters + explicit predicate: M02's vigilance scoring and M13's nightly jobs
        // read this with no ambient tenant.
        var row = await db.ComplianceSettings
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .Select(s => new ComplianceSettingsDto(
                s.FatcaEnabled,
                s.FatcaRegime == null ? null : s.FatcaRegime.ToString(),
                s.FatcaGiin,
                s.FatcaRequiredAt.ToString(),
                s.FatcaForCoHolders,
                s.PepEnabled,
                s.PepPostMandateMonths,
                s.KycAutoApprovalEnabled,
                s.KycAutoApprovalMaxTier,
                s.KycAutoApprovalSamplePct))
            .FirstOrDefaultAsync(ct);

        // No row means the tenant has never opened the compliance screen, which is the same thing
        // as running on the platform defaults — so answer with them rather than null. Materialising
        // a row here would make a READ write, and three modules call this on hot paths.
        return row ?? PlatformDefaultCompliance;
    }

    /// <summary>
    /// Must stay in step with <c>ComplianceSettings.CreateDefault</c>. Two declarations of one set
    /// of defaults is a real risk, and the alternative — building the entity here just to project
    /// it — would pull the domain type into a projection that exists to keep it out.
    /// <c>ComplianceSettingsDefaultsTests</c> pins the two together.
    /// </summary>
    private static readonly ComplianceSettingsDto PlatformDefaultCompliance = new(
        FatcaEnabled:         false,
        FatcaRegime:          null,
        FatcaGiin:            null,
        FatcaRequiredAt:      nameof(Domain.FatcaRequiredAt.Conversion),
        FatcaForCoHolders:    true,
        // TRUE, matching CreateDefault: a tenant with no row keeps PEP vigilance, because the
        // alternative is switching off an LBC/FT obligation as a side effect of a deployment.
        PepEnabled:           true,
        PepPostMandateMonths: Domain.ComplianceSettings.DefaultPepPostMandateMonths,
        // FALSE, matching CreateDefault, and the opposite reasoning to PepEnabled's: a tenant with
        // no row has never adopted machine approval, and an absent row must not be read as consent
        // to remove the human from a KYC circuit.
        KycAutoApprovalEnabled:   false,
        KycAutoApprovalMaxTier:   Domain.KycRemotePolicyValues.SimplifiedTier,
        KycAutoApprovalSamplePct: Domain.ComplianceSettings.DefaultKycAutoApprovalSamplePct);

    public async Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0) return new Dictionary<Guid, string>();
        var ids = userIds.Distinct().ToList();
        return await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId && ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
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
